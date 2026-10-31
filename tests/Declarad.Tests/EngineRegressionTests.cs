using Declarad.Config;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.Dns;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>Regression tests for bugs found in review of planning, applying and resource types.</summary>
    public class EngineRegressionTests
    {
        private const string DomainAdmins = "CN=Domain Admins,CN=Users,DC=corp,DC=local";
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null, Connect = _ => _dir });

        private Plan Plan(string src, PlanOptions? options = null) => new Planner(_dir, Load(src), _state, options ?? new PlanOptions()).Build();

        private ApplyResult Apply(string src, PlanOptions? options = null, bool expectSuccess = true)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(src, options));
            if (expectSuccess)
            {
                Assert.True(result.Success, string.Join("\n", result.Errors));
            }
            return result;
        }

        private void AssertConverged(string src) =>
            Assert.Empty(Plan(src).Changes.Where(c => c.Action != ChangeAction.NoOp).Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old}->{x.New}"))}"));

        private static string Error(Action action)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(action);
            return ex is ConfigErrorsException many ? string.Join("\n", many.Errors.Select(e => e.Message)) : ex.Message;
        }

        [Fact]
        public void AdditiveRemovalFollowsRenamesInTheSameApply()
        {
            Apply("""
                user "a" { enabled = false }
                user "b" { enabled = false }
                group "vpn" { name = "VPN" }
                user "c" {
                  enabled = false
                  groups  = [group.vpn.dn]
                }
                group_membership "da" {
                  group   = "Domain Admins"
                  members = [user.a.dn, user.b.dn]
                }
                """);
            const string next = """
                user "a" {
                  name    = "a-renamed"
                  enabled = false
                }
                user "b" { enabled = false }
                group "vpn" { name = "VPN Users" }
                user "c" {
                  enabled = false
                  groups  = []
                }
                group_membership "da" {
                  group   = "Domain Admins"
                  members = [user.b.dn]
                }
                """;
            Apply(next);
            Assert.DoesNotContain("CN=a-renamed,CN=Users,DC=corp,DC=local", _dir.Get(DomainAdmins)!.GetAll("member"));
            Assert.Empty(_dir.Get("CN=c,CN=Users,DC=corp,DC=local")!.GetAll("memberOf"));
            AssertConverged(next);
        }

        [Fact]
        public void AdditiveListsNeverRemoveMembershipsDeclaradDidNotAdd()
        {
            _dir.Seed("CN=Administrator,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "Administrator"));
            _dir.SetAttribute(DomainAdmins, "member", "CN=Administrator,CN=Users,DC=corp,DC=local");
            Apply("""
                user "ops" { enabled = false }
                group_membership "da" {
                  group   = "Domain Admins"
                  members = ["Administrator", user.ops.dn]
                }
                """);
            Assert.Equal(2, _dir.Get(DomainAdmins)!.GetAll("member").Length);
            Apply("user \"ops\" { enabled = false }");
            Assert.Equal(new[] { "CN=Administrator,CN=Users,DC=corp,DC=local" }, _dir.Get(DomainAdmins)!.GetAll("member"));
        }

        [Fact]
        public void IgnoredNameAndPathAreNotRestoredByOtherUpdates()
        {
            const string src = """
                ou "a" { protected_from_accidental_deletion = false }
                ou "b" { protected_from_accidental_deletion = false }
                group "g" {
                  path        = ou.a.dn
                  description = "one"
                  members     = []
                  lifecycle {
                    ignore_changes = [path, name]
                  }
                }
                """;
            Apply(src);
            _dir.Rename("CN=g,OU=a,DC=corp,DC=local", "CN=helpdesk-name", "OU=b,DC=corp,DC=local");
            Apply(src.Replace("\"one\"", "\"two\""));
            Assert.True(_dir.Exists("CN=helpdesk-name,OU=b,DC=corp,DC=local"));
            Assert.Equal("two", _dir.Attr("CN=helpdesk-name,OU=b,DC=corp,DC=local", "description"));
        }

        [Fact]
        public void AdoptRefusesObjectsManagedUnderAnotherAddress()
        {
            Apply("group \"a\" { name = \"X\" }");
            string message = Error(() => Plan("group \"a\" { name = \"Y\" }\ngroup \"b\" { name = \"X\" }", new PlanOptions { Adopt = true }));
            Assert.Contains("already managed as group.a", message);
        }

        [Fact]
        public void DeletesThatFreeANameRunFirst()
        {
            Apply("group \"a\" { name = \"X\" }\ngroup \"b\" { name = \"Y\" }");
            var plan = Plan("group \"b\" { name = \"X\" }");
            Assert.True(plan.Changes[0].Early);
            Apply("group \"b\" { name = \"X\" }");
            Assert.True(_dir.Exists("CN=X,CN=Users,DC=corp,DC=local"));
            Assert.False(_dir.Exists("CN=Y,CN=Users,DC=corp,DC=local"));
        }

        [Fact]
        public void GpoLinkOrderConvergesInOneApply()
        {
            const string src = """
                ou "o" {}
                gpo_link "x1" {
                  gpo    = "{11111111-1111-1111-1111-111111111111}"
                  target = ou.o.dn
                  order  = 3
                }
                gpo_link "x2" {
                  gpo    = "{22222222-2222-2222-2222-222222222222}"
                  target = ou.o.dn
                  order  = 2
                }
                gpo_link "x3" {
                  gpo    = "{33333333-3333-3333-3333-333333333333}"
                  target = ou.o.dn
                  order  = 1
                }
                """;
            Apply(src);
            var links = GpoLinkResource.ReadLinks(_dir.Attr("OU=o,DC=corp,DC=local", "gPLink"));
            Assert.Equal(new[] { "{33333333-3333-3333-3333-333333333333}", "{22222222-2222-2222-2222-222222222222}", "{11111111-1111-1111-1111-111111111111}" }, links.Select(l => l.Gpo));
            AssertConverged(src);
        }

        [Fact]
        public void GroupMembershipOnAnAuthoritativeGroupDoesNotFight()
        {
            const string src = """
                user "a" { enabled = false }
                user "b" { enabled = false }
                group "g" { members = [user.a.dn] }
                group_membership "m" {
                  group   = group.g.dn
                  members = [user.b.dn]
                }
                """;
            Apply(src);
            Assert.Equal(2, _dir.Get("CN=g,CN=Users,DC=corp,DC=local")!.GetAll("member").Length);
            AssertConverged(src);
        }

        [Fact]
        public void DnsConflictsAreRejectedAndApexChangesBumpTheSerial()
        {
            Assert.Contains("apex NS records are managed by dns_zone.z", Error(() => Load("""
                dns_zone "z" { name = "z.example" }
                dns_record "ns" {
                  zone    = dns_zone.z.name
                  name    = "@"
                  type    = "NS"
                  records = ["ns1.other.example."]
                }
                """)));
            Assert.Contains("CNAME cannot share its name", Error(() => Load("""
                dns_record "c" {
                  zone    = "corp.local"
                  name    = "x"
                  type    = "CNAME"
                  records = ["y"]
                }
                dns_record "t" {
                  zone    = "corp.local"
                  name    = "x"
                  type    = "TXT"
                  records = ["hi"]
                }
                """)));

            Apply("dns_record \"mx\" {\n  zone = \"corp.local\"\n  name = \"@\"\n  type = \"MX\"\n  records = [\"10 mail\"]\n}");
            var apex = DnsCodec.ReadApexSoa(_dir, "DC=corp.local,CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=local")!.Value;
            Assert.Equal(8u, apex.Soa.Serial);
            Assert.Equal(1u, DnsCodec.NextSerial(uint.MaxValue));
        }

        [Fact]
        public void AdoptsZonesWhoseNamesDifferOnlyInCase()
        {
            _dir.Add("DC=Lab.Example,CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=local", new Dictionary<string, object[]> { ["objectClass"] = new object[] { "dnsZone" } });
            var plan = Plan("dns_zone \"z\" {\n  name = \"lab.example\"\n}", new PlanOptions { Adopt = true });
            Assert.DoesNotContain(plan.Changes.Single().Changes, c => c.Name == "name");
        }

        [Theory]
        [InlineData("dns_record \"m\" {\n  zone = \"corp.local\"\n  name = \"m\"\n  type = \"MX\"\n  records = [\"10 LONGLABEL\"]\n}", "not a valid host name")]
        [InlineData("dns_record \"a\" {\n  zone = \"corp.local\"\n  name = \"a\"\n  type = \"A\"\n  records = [\"10\"]\n}", "not an IPv4 address")]
        [InlineData("dns_record \"a\" {\n  zone = \"corp.local\"\n  name = \"a\"\n  type = \"A\"\n  records = [\"10.0.0.1\"]\n  ttl = 1e20\n}", "")]
        [InlineData("password_policy \"p\" {\n  precedence = 1\n  max_password_age = \"99999999999999999999d\"\n}", "not a valid duration")]
        [InlineData("password_policy \"p\" {\n  precedence = 1\n  min_password_length = 7.5\n}", "must be a whole number")]
        public void RejectsInvalidValuesWithConfigurationErrors(string src, string message)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load(src.Replace("LONGLABEL", new string('a', 300))));
            string text = ex is ConfigErrorsException many ? string.Join("\n", many.Errors.Select(e => e.Message)) : ex.Message;
            Assert.Contains(message, text);
        }

        [Fact]
        public void PreventDestroyBlocksReplacingAnAttachment()
        {
            const string src = """
                ou "a" { protected_from_accidental_deletion = false }
                ou "b" { protected_from_accidental_deletion = false }
                gpo_link "l" {
                  gpo    = "{11111111-1111-1111-1111-111111111111}"
                  target = ou.a.dn
                  lifecycle {
                    prevent_destroy = true
                  }
                }
                """;
            Apply(src);
            Assert.Contains("prevent_destroy", Error(() => Plan(src.Replace("target = ou.a.dn", "target = ou.b.dn"))));
        }

        [Fact]
        public void TargetedDestroyIncludesChildrenThatOnlyExistInState()
        {
            Apply("""
                ou "a" { protected_from_accidental_deletion = false }
                group "g" { path = ou.a.dn }
                """);
            var config = Load("ou \"a\" { protected_from_accidental_deletion = false }");
            var plan = new Planner(_dir, config, _state, new PlanOptions { Destroy = true, Targets = new[] { "ou.a" } }).Build();
            Assert.Equal(new[] { "group.g", "ou.a" }, plan.Changes.Where(c => c.Action == ChangeAction.Delete).Select(c => c.Address));
            var result = new Applier(_dir, _state, _ => { }, NullLogger.Instance).Apply(plan);
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }

        [Fact]
        public void AdoptedDeferredValuesAreRecordedSoRemovingThemClears()
        {
            _dir.Seed("CN=boss,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "boss"));
            _dir.Seed("CN=emp,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "emp"), ("manager", "CN=boss,CN=Users,DC=corp,DC=local"));
            Apply("user \"emp\" {\n  manager = \"boss\"\n  enabled = false\n}", new PlanOptions { Adopt = true });
            Assert.Equal(Value.From("CN=boss,CN=Users,DC=corp,DC=local"), _state.Find("user.emp")!.Get("manager"));
            Apply("user \"emp\" {\n  enabled = false\n}");
            Assert.Null(_dir.Attr("CN=emp,CN=Users,DC=corp,DC=local", "manager"));
        }

        [Fact]
        public void EmulatorEnforcesNameUniquenessLikeAd()
        {
            _dir.Seed("CN=jdoe,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "jdoe"));
            var ex = Assert.Throws<Declarad.Directory.DirectoryException>(() =>
                _dir.Seed("CN=other,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "JDOE")));
            Assert.Contains("already in use", ex.Message);
            Assert.Equal("jdoe", _dir.Attr("CN=jdoe,CN=Users,DC=corp,DC=local", "cn"));
        }
    }
}
