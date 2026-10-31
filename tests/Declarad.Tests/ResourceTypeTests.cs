using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Hcl.Values;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class ResourceTypeTests
    {
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = _ => null, Connect = _ => _dir });

        private Plan Plan(string src, PlanOptions? options = null) => new Planner(_dir, Load(src), _state, options ?? new PlanOptions()).Build();

        private Plan Plan(Configuration config, PlanOptions? options = null) => new Planner(_dir, config, _state, options ?? new PlanOptions()).Build();

        private ApplyResult Apply(Plan plan)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(plan);
            Assert.True(result.Success, string.Join("\n", result.Errors));
            return result;
        }

        private ApplyResult Apply(string src, PlanOptions? options = null) => Apply(Plan(src, options));

        private void AssertConverged(string src)
        {
            var pending = Plan(src).Changes.Where(c => c.Action != ChangeAction.NoOp || c.Adopt)
                .Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old} -> {x.New} +[{string.Join(",", x.Added)}] -[{string.Join(",", x.Removed)}]"))}");
            Assert.Empty(pending);
        }

        [Fact]
        public void ManagesComputersContactsAndContainers()
        {
            const string src = """
                container "servers" {
                  description = "Server accounts"
                }
                computer "web01" {
                  path                    = container.servers.dn
                  description             = "Web server"
                  service_principal_names = ["HTTP/web01.corp.local"]
                  groups                  = [group.web.dn]
                }
                computer "web02" {
                  path    = container.servers.dn
                  enabled = false
                }
                group "web" {}
                contact "vendor" {
                  given_name = "Val"
                  surname    = "Vendor"
                  email      = "val@vendor.example"
                  groups     = [group.web.dn]
                }
                """;
            Apply(src);
            const string web01 = "CN=web01,CN=servers,DC=corp,DC=local";
            Assert.Equal("4128", _dir.Attr(web01, "userAccountControl"));
            Assert.Equal("4130", _dir.Attr("CN=web02,CN=servers,DC=corp,DC=local", "userAccountControl"));
            Assert.Equal("WEB01$", _dir.Attr(web01, "sAMAccountName"));
            Assert.Equal("web01.corp.local", _dir.Attr(web01, "dNSHostName"));
            Assert.Equal(2, _dir.Get("CN=web,CN=Users,DC=corp,DC=local")!.GetAll("member").Length);
            Assert.Equal("val@vendor.example", _dir.Attr("CN=vendor,CN=Users,DC=corp,DC=local", "mail"));
            AssertConverged(src);

            // A computer is also a "user" object in AD, but must never be treated as a user resource.
            const string asUser = "user \"web01\" {\n  name = \"web01\"\n  path = \"CN=servers,DC=corp,DC=local\"\n}";
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan(asUser, new PlanOptions { Adopt = true }));
            Assert.Contains("already managed as computer.web01", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
            var saved = _state;
            _state = new StateFile();
            ex = Assert.ThrowsAny<DeclaradException>(() => Plan(asUser, new PlanOptions { Adopt = true }));
            Assert.Contains("is a computer, not a user", ex is ConfigErrorsException c3 ? c3.Errors[0].Message : ex.Message);
            _state = saved;

            Apply(src.Replace("enabled = false", "enabled = true"));
            Assert.Equal("4128", _dir.Attr("CN=web02,CN=servers,DC=corp,DC=local", "userAccountControl"));
        }

        [Fact]
        public void ManagesServiceAccountsWithPasswordRetrievers()
        {
            const string src = """
                computer "app01" {}
                computer "app02" {}
                group "app_servers" {
                  members = [computer.app02.dn]
                }
                service_account "svc_app" {
                  service_principal_names                 = ["HTTP/app.corp.local"]
                  kerberos_encryption_types               = ["aes256", "aes128"]
                  principals_allowed_to_retrieve_password = [computer.app01.dn, "app_servers"]
                }
                """;
            Apply(src);
            const string svc = "CN=svc_app,CN=Managed Service Accounts,DC=corp,DC=local";
            var obj = _dir.Get(svc)!;
            Assert.Equal("svc_app$", obj.Get("sAMAccountName"));
            Assert.Equal("24", obj.Get("msDS-SupportedEncryptionTypes"));
            var sids = SecurityDescriptor.AllowedSids(obj.Binary["msDS-GroupMSAMembership"][0]);
            Assert.Equal(2, sids.Count);
            Assert.Contains(_dir.Get("CN=app01,CN=Computers,DC=corp,DC=local")!.Sid, sids);
            AssertConverged(src);

            string fewer = src.Replace("[computer.app01.dn, \"app_servers\"]", "[\"app_servers\"]");
            var change = Plan(fewer).Changes.Single(c => c.Address == "service_account.svc_app").Get("principals_allowed_to_retrieve_password")!;
            Assert.Equal(new[] { "CN=app01,CN=Computers,DC=corp,DC=local" }, change.Removed);
            Apply(fewer);
            Assert.Single(SecurityDescriptor.AllowedSids(_dir.Get(svc)!.Binary["msDS-GroupMSAMembership"][0]));
            AssertConverged(fewer);

            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("service_account \"x\" {}", new PlanOptions { Encrypted = false }));
            Assert.Contains("encrypted", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void ManagesFineGrainedPasswordPolicies()
        {
            const string src = """
                group "admins" {}
                password_policy "admins" {
                  precedence          = 10
                  min_password_length = 15
                  max_password_age    = "1008h"
                  lockout_threshold   = 5
                  lockout_duration    = "never"
                  applies_to          = [group.admins.dn]
                }
                """;
            var config = Load(src);
            Assert.Equal(Value.From("42d"), config.Find("password_policy.admins")!.Get("max_password_age"));
            Apply(Plan(config));
            const string pso = "CN=admins,CN=Password Settings Container,CN=System,DC=corp,DC=local";
            Assert.Equal("-36288000000000", _dir.Attr(pso, "msDS-MaximumPasswordAge"));
            Assert.Equal("-9223372036854775808", _dir.Attr(pso, "msDS-LockoutDuration"));
            Assert.Equal("-864000000000", _dir.Attr(pso, "msDS-MinimumPasswordAge"));
            Assert.Equal("TRUE", _dir.Attr(pso, "msDS-PasswordComplexityEnabled"));
            Assert.Equal("CN=admins,CN=Users,DC=corp,DC=local", _dir.Attr(pso, "msDS-PSOAppliesTo"));
            AssertConverged(src);

            Apply(src.Replace("\"1008h\"", "\"90d\""));
            Assert.Equal("-77760000000000", _dir.Attr(pso, "msDS-MaximumPasswordAge"));

            var ex = Assert.ThrowsAny<DeclaradException>(() => Load(src.Replace("\"never\"", "\"soon\"")));
            Assert.Contains("not a valid duration", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void GroupMembershipIsAdditiveOnUnmanagedGroups()
        {
            _dir.Seed("CN=existing,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "existing"));
            const string admins = "CN=Domain Admins,CN=Users,DC=corp,DC=local";
            _dir.SetAttribute(admins, "member", "CN=existing,CN=Users,DC=corp,DC=local");

            const string src = """
                user "a" { enabled = false }
                user "b" { enabled = false }
                group_membership "admins" {
                  group   = "Domain Admins"
                  members = [user.a.dn, user.b.dn]
                }
                """;
            var plan = Plan(src);
            var membership = plan.Changes.Single(c => c.Address == "group_membership.admins");
            Assert.Equal(ChangeAction.Create, membership.Action);
            Assert.Equal(plan.Changes.Count - 1, plan.Changes.IndexOf(membership));
            Apply(plan);
            Assert.Equal(3, _dir.Get(admins)!.GetAll("member").Length);
            AssertConverged(src);

            Apply(src.Replace("[user.a.dn, user.b.dn]", "[user.a.dn]"));
            Assert.Equal(2, _dir.Get(admins)!.GetAll("member").Length);

            // Removing the resource detaches only what it added; the pre-existing member stays.
            Apply("user \"a\" { enabled = false }\nuser \"b\" { enabled = false }");
            Assert.Equal(new[] { "CN=existing,CN=Users,DC=corp,DC=local" }, _dir.Get(admins)!.GetAll("member"));
            Assert.Null(_state.Find("group_membership.admins"));
        }

        [Fact]
        public void LinksGposWithOrderEnforcementAndDetach()
        {
            const string src = """
                data "gpo" "default" {
                  name = "Default Domain Policy"
                }
                data "gpo" "baseline" {
                  name = "Security Baseline"
                }
                ou "staff" {
                  block_inheritance = true
                }
                gpo_link "default" {
                  gpo    = data.gpo.default.id
                  target = ou.staff.dn
                }
                gpo_link "baseline" {
                  gpo      = data.gpo.baseline.id
                  target   = ou.staff.dn
                  enforced = true
                  order    = 1
                }
                """;
            Apply(src);
            const string ou = "OU=staff,DC=corp,DC=local";
            string gpLink = _dir.Attr(ou, "gPLink")!;
            Assert.EndsWith($"[LDAP://cn={InMemoryDirectory.BaselinePolicy},cn=policies,cn=system,DC=corp,DC=local;2]", gpLink);
            Assert.StartsWith($"[LDAP://cn={InMemoryDirectory.DefaultDomainPolicy}", gpLink);
            Assert.Equal("1", _dir.Attr(ou, "gPOptions"));
            AssertConverged(src);

            // Someone disables a link by hand: declarad restores it.
            _dir.SetAttribute(ou, "gPLink", gpLink.Replace(";2]", ";3]"));
            var drift = Plan(src).Changes.Single(c => c.Address == "gpo_link.baseline");
            Assert.Equal("enabled", Assert.Single(drift.Changes).Name);
            Apply(src);
            AssertConverged(src);

            // Moving the link to another OU detaches it from the first.
            string moved = src.Replace("ou \"staff\" {", "ou \"other\" {}\nou \"staff\" {").Replace("target   = ou.staff.dn", "target   = ou.other.dn");
            var replace = Plan(moved).Changes.Single(c => c.Address == "gpo_link.baseline");
            Assert.Equal(ChangeAction.Replace, replace.Action);
            Apply(moved);
            Assert.DoesNotContain(InMemoryDirectory.BaselinePolicy, _dir.Attr(ou, "gPLink"));
            Assert.Contains(InMemoryDirectory.BaselinePolicy, _dir.Attr("OU=other,DC=corp,DC=local", "gPLink"));
            AssertConverged(moved);
        }

        [Fact]
        public void MovedAndImportBlocksTransferOwnership()
        {
            Apply("group \"old\" { name = \"Ops\" }");
            int writes = _dir.WriteCount;
            const string renamed = """
                group "ops" { name = "Ops" }
                moved {
                  from = group.old
                  to   = group.ops
                }
                """;
            var change = Assert.Single(Plan(renamed).Changes);
            Assert.Contains("moved from group.old", change.Notes);
            Apply(renamed);
            Assert.Equal(writes, _dir.WriteCount);
            Assert.NotNull(_state.Find("group.ops"));
            AssertConverged(renamed);

            _dir.Seed("CN=legacy,CN=Users,DC=corp,DC=local", "group", ("sAMAccountName", "legacy"), ("groupType", "-2147483646"));
            const string imported = """
                group "legacy" { description = "now managed" }
                import {
                  to = group.legacy
                  id = "CN=legacy,CN=Users,DC=corp,DC=local"
                }
                """;
            var plan = Plan(imported);
            var legacy = plan.Changes.Single(c => c.Address == "group.legacy");
            Assert.True(legacy.Adopt);
            Assert.Equal(ChangeAction.Update, legacy.Action);
            Apply(plan);
            Assert.Equal("now managed", _dir.Attr("CN=legacy,CN=Users,DC=corp,DC=local", "description"));
        }

        [Fact]
        public void AppliesModulesEndToEnd()
        {
            var config = ModuleTests.LoadTree(new()
            {
                ["main.dad"] = """
                    module "eng" {
                      source = "./modules/dept"
                      name   = "Engineering"
                      users  = ["jdoe", "asmith"]
                    }
                    """,
                ["modules/dept/main.dad"] = """
                    variable "name" { type = string }
                    variable "users" { type = set(string) }
                    ou "dept" {
                      name                               = var.name
                      protected_from_accidental_deletion = false
                    }
                    user "u" {
                      for_each         = var.users
                      sam_account_name = each.key
                      path             = ou.dept.dn
                      enabled  = false
                    }
                    group "all" {
                      name    = "${var.name} All"
                      path    = ou.dept.dn
                      members = values(user.u)[*].dn
                    }
                    """,
            }, connect: _ => _dir);
            Apply(Plan(config));
            Assert.Equal(2, _dir.Get("CN=Engineering All,OU=Engineering,DC=corp,DC=local")!.GetAll("member").Length);
            Assert.NotNull(_state.Find("module.eng.user.u[\"jdoe\"]"));
            Assert.DoesNotContain(Plan(config).Changes, c => c.Action != ChangeAction.NoOp);

            var destroy = Plan(config, new PlanOptions { Destroy = true });
            Assert.Equal(4, destroy.ToDestroy);
            Apply(destroy);
            Assert.False(_dir.Exists("OU=Engineering,DC=corp,DC=local"));
        }

        [Fact]
        public void TargetLimitsThePlan()
        {
            var plan = Plan("group \"a\" {}\ngroup \"b\" {}", new PlanOptions { Targets = new[] { "group.a" } });
            Assert.Equal("group.a", Assert.Single(plan.Changes).Address);
        }

        [Fact]
        public void ExportsNewTypesAndGpoLinksForAdoption()
        {
            Apply("""
                data "gpo" "baseline" {
                  name = "Security Baseline"
                }
                ou "srv" {}
                computer "web01" {
                  path = ou.srv.dn
                }
                contact "vendor" {
                  email = "v@example.com"
                }
                gpo_link "srv" {
                  gpo      = data.gpo.baseline.id
                  target   = ou.srv.dn
                  enforced = true
                }
                password_policy "strict" {
                  precedence = 1
                }
                """);
            string hcl = new Exporter(_dir).Export(null, Exporter.DefaultTypes.ToList());
            Assert.Contains("computer \"web01\"", hcl);
            Assert.Contains("gpo_link \"srv_security_baseline\" { # GPO: Security Baseline", hcl);
            Assert.Contains("target   = ou.srv.dn", hcl);

            _state = new StateFile();
            var config = ConfigLoader.LoadSources(".", new[] { ("c.dad", ConfigTests.Connection), ("export.dad", hcl) }, new LoadOptions { GetEnv = _ => null });
            var plan = Plan(config, new PlanOptions { Adopt = true });
            var unexpected = plan.Changes.Where(c => c.Action is not (ChangeAction.NoOp or ChangeAction.Create) || (c.Action == ChangeAction.Create && !c.IsAttachment))
                .Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => x.Name))}");
            Assert.Empty(unexpected);
            // The exported link already exists, so "creating" it is a no-op attach.
            Apply(plan);
            AssertConvergedConfig(config);
        }

        private void AssertConvergedConfig(Configuration config)
        {
            Assert.Empty(Plan(config).Changes.Where(c => c.Action != ChangeAction.NoOp).Select(c => c.Address));
        }
    }
}
