using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class EngineTests
    {
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Plan Plan(string src, PlanOptions? options = null) =>
            new Planner(_dir, ConfigTests.Load(src), _state, options ?? new PlanOptions()).Build();

        private ApplyResult Apply(string src, PlanOptions? options = null)
        {
            var plan = Plan(src, options);
            // Round-trip the state through JSON on every save, as the CLI does.
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(plan);
            Assert.True(result.Success, string.Join("\n", result.Errors));
            return result;
        }

        private void AssertNoChanges(string src)
        {
            var plan = Plan(src);
            var pending = plan.Changes.Where(c => c.Action != ChangeAction.NoOp || c.Adopt)
                .Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old} -> {x.New}"))}");
            Assert.Empty(pending);
        }

        private const string Basic = """
            ou "staff" {
              name        = "Staff"
              description = "All staff"
            }
            ou "eng" {
              name = "Engineering"
              path = ou.staff.dn
            }
            group "devs" {
              name    = "Developers"
              path    = ou.eng.dn
              members = [user.jdoe.dn, "asmith"]
            }
            user "jdoe" {
              sam_account_name = "jdoe"
              given_name       = "John"
              surname          = "Doe"
              path             = ou.eng.dn
              title            = "Engineer"
              initial_password = "P@ssw0rd!"
              groups           = ["Domain Admins"]
            }
            user "asmith" {
              sam_account_name = "asmith"
              given_name       = "Anna"
              surname          = "Smith"
              path             = ou.eng.dn
              enabled          = false
              manager          = user.jdoe.dn
            }
            """;

        [Fact]
        public void CreatesEverythingInDependencyOrderThenConverges()
        {
            var plan = Plan(Basic);
            var order = plan.Changes.Select(c => c.Address).ToList();
            Assert.True(order.IndexOf("ou.staff") < order.IndexOf("ou.eng"));
            Assert.True(order.IndexOf("ou.eng") < order.IndexOf("user.jdoe"));
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.Create, c.Action));

            var result = Apply(Basic);
            Assert.Equal(5, result.Created);

            const string jdoe = "CN=John Doe,OU=Engineering,OU=Staff,DC=corp,DC=local";
            const string asmith = "CN=Anna Smith,OU=Engineering,OU=Staff,DC=corp,DC=local";
            const string devs = "CN=Developers,OU=Engineering,OU=Staff,DC=corp,DC=local";
            Assert.Equal("512", _dir.Attr(jdoe, "userAccountControl"));
            Assert.Equal("514", _dir.Attr(asmith, "userAccountControl"));
            Assert.Equal(jdoe, _dir.Attr(asmith, "manager"));
            Assert.Equal(2, _dir.Get(devs)!.GetAll("member").Length);
            Assert.Contains("CN=Domain Admins,CN=Users,DC=corp,DC=local", _dir.Get(jdoe)!.GetAll("memberOf"));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get("OU=Staff,DC=corp,DC=local")!.SecurityDescriptor));
            Assert.Equal(GroupResource.GroupType("global", "security").ToString(), _dir.Attr(devs, "groupType"));

            Assert.Equal(5, _state.Resources.Count);
            Assert.DoesNotContain(_state.Resources, r => r.Attributes.ContainsKey("initial_password"));
            AssertNoChanges(Basic);
        }

        [Fact]
        public void UpdatesClearsAndDetectsDrift()
        {
            Apply(Basic);
            const string jdoe = "CN=John Doe,OU=Engineering,OU=Staff,DC=corp,DC=local";

            // Drift made outside declarad is reverted.
            _dir.SetAttribute(jdoe, "title", "Hacker");
            var plan = Plan(Basic);
            var change = Assert.Single(plan.Changes, c => c.Action == ChangeAction.Update);
            Assert.Equal("title", Assert.Single(change.Changes).Name);
            Apply(Basic);
            Assert.Equal("Engineer", _dir.Attr(jdoe, "title"));

            // Removing a previously managed attribute clears it; attributes never managed are left alone.
            _dir.SetAttribute(jdoe, "info", "set by helpdesk");
            string src = Basic.Replace("  title            = \"Engineer\"\n", "");
            plan = Plan(src);
            var titleChange = plan.Changes.Single(c => c.Address == "user.jdoe").Changes.Single();
            Assert.Equal(("title", true), (titleChange.Name, titleChange.New.IsNull));
            Apply(src);
            Assert.Null(_dir.Attr(jdoe, "title"));
            Assert.Equal("set by helpdesk", _dir.Attr(jdoe, "info"));
        }

        [Fact]
        public void EnablingAccountWithoutPasswordFailsAndStateStaysConsistent()
        {
            Apply(Basic);
            string src = Basic.Replace("enabled          = false", "enabled          = true");
            var plan = Plan(src);
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(plan);
            Assert.False(result.Success);
            Assert.Contains("user.asmith", result.Errors[0]);
            Assert.Equal(5, _state.Resources.Count);
        }

        [Fact]
        public void RenamingParentOuMovesChildrenWithoutDoubleMove()
        {
            Apply(Basic);
            string src = Basic.Replace("name = \"Engineering\"", "name = \"R&D\"");
            var plan = Plan(src);
            Assert.Equal(ChangeAction.Update, plan.Changes.Single(c => c.Address == "ou.eng").Action);
            Apply(src);

            Assert.True(_dir.Exists("CN=John Doe,OU=R&D,OU=Staff,DC=corp,DC=local"));
            Assert.False(_dir.Exists("OU=Engineering,OU=Staff,DC=corp,DC=local"));
            Assert.Equal("CN=John Doe,OU=R&D,OU=Staff,DC=corp,DC=local",
                _dir.Get("CN=Developers,OU=R&D,OU=Staff,DC=corp,DC=local")!.GetAll("member").First(m => m.StartsWith("CN=John")));
            AssertNoChanges(src);
        }

        [Fact]
        public void MovesProtectedOuByTemporarilyLiftingProtection()
        {
            const string src = """
                ou "a" {
                  name = "a"
                }
                ou "b" {
                  name = "b"
                }
                ou "child" {
                  name = "child"
                  path = ou.a.dn
                }
                """;
            Apply(src);
            string moved = src.Replace("path = ou.a.dn", "path = ou.b.dn");
            Apply(moved);
            var child = _dir.Get("OU=child,OU=b,DC=corp,DC=local");
            Assert.NotNull(child);
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(child!.SecurityDescriptor));
            AssertNoChanges(moved);
        }

        [Fact]
        public void DeletesRemovedResourcesChildrenFirstAndRespectsProtection()
        {
            const string src = """
                ou "a" {
                  name = "a"
                  protected_from_accidental_deletion = false
                }
                ou "b" {
                  name = "b"
                  path = ou.a.dn
                  protected_from_accidental_deletion = false
                }
                user "u" {
                  sam_account_name = "u"
                  path    = ou.b.dn
                  enabled = false
                }
                ou "keep" {
                  name = "keep"
                }
                """;
            Apply(src);
            var plan = Plan("ou \"keep\" {\n  name = \"keep\"\n}");
            Assert.Equal(new[] { "user.u", "ou.b", "ou.a" }, plan.Changes.Where(c => c.Action == ChangeAction.Delete).Select(c => c.Address));
            var result = Apply("ou \"keep\" {\n  name = \"keep\"\n}");
            Assert.Equal(3, result.Deleted);
            Assert.False(_dir.Exists("OU=a,DC=corp,DC=local"));

            // A protected OU cannot be removed until protection is turned off.
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("ou \"other\" {\n  name = \"other\"\n}"));
            Assert.Contains("protected from accidental deletion", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void PreventDestroyBlocksDeletion()
        {
            Apply("""
                group "g" {
                  name = "g"
                  lifecycle { prevent_destroy = true }
                }
                """);
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("", new PlanOptions { Destroy = true }));
            Assert.Contains("prevent_destroy", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void ExistingObjectsNeedImportOrAdopt()
        {
            _dir.Seed("CN=legacy,CN=Users,DC=corp,DC=local", "group", ("sAMAccountName", "legacy"), ("groupType", "-2147483646"), ("description", "old"));
            const string src = """
                group "legacy" {
                  name = "legacy"
                  description = "new"
                }
                """;
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan(src));
            Assert.Contains("declarad import", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);

            var plan = Plan(src, new PlanOptions { Adopt = true });
            var change = Assert.Single(plan.Changes);
            Assert.True(change.Adopt);
            Assert.Equal(ChangeAction.Update, change.Action);
            Apply(src, new PlanOptions { Adopt = true });
            Assert.Equal("new", _dir.Attr("CN=legacy,CN=Users,DC=corp,DC=local", "description"));
            AssertNoChanges(src);
        }

        [Fact]
        public void RecreatesObjectsDeletedOutsideAndForgetsVanishedOnes()
        {
            const string src = """
                group "a" {
                  name = "a"
                }
                group "b" {
                  name = "b"
                }
                """;
            Apply(src);
            _dir.DeleteOutOfBand("CN=a,CN=Users,DC=corp,DC=local");
            var plan = Plan(src);
            var a = plan.Changes.Single(c => c.Address == "group.a");
            Assert.Equal(ChangeAction.Create, a.Action);
            Assert.Contains(a.Notes, n => n.Contains("deleted outside"));

            _dir.DeleteOutOfBand("CN=b,CN=Users,DC=corp,DC=local");
            plan = Plan("group \"a\" {\n  name = \"a\"\n}");
            Assert.Equal(ChangeAction.Forget, plan.Changes.Single(c => c.Address == "group.b").Action);
            Apply("group \"a\" {\n  name = \"a\"\n}");
            Assert.Null(_state.Find("group.b"));
        }

        [Fact]
        public void RenamingTheBlockLabelMovesStateInsteadOfRecreating()
        {
            Apply("group \"old\" { name = \"Ops\" }");
            const string src = "group \"new_label\" { name = \"Ops\" }";
            var plan = Plan(src);
            var change = Assert.Single(plan.Changes);
            Assert.Equal(ChangeAction.NoOp, change.Action);
            Assert.True(change.Adopt);
            int writes = _dir.WriteCount;
            Apply(src);
            Assert.Equal(writes, _dir.WriteCount);
            Assert.NotNull(_state.Find("group.new_label"));
            Assert.Null(_state.Find("group.old"));
        }

        [Fact]
        public void UserGroupsAreAdditiveAndMergeIntoAuthoritativeGroups()
        {
            _dir.Seed("CN=VPN,CN=Users,DC=corp,DC=local", "group", ("sAMAccountName", "VPN"), ("groupType", "-2147483646"));
            _dir.Seed("CN=Printers,CN=Users,DC=corp,DC=local", "group", ("sAMAccountName", "Printers"), ("groupType", "-2147483646"));
            const string src = """
                group "team" {
                  name = "team"
                  members = []
                }
                user "u" {
                  sam_account_name = "u"
                  enabled = false
                  groups  = ["VPN", group.team.dn]
                }
                """;
            Apply(src);
            const string u = "CN=u,CN=Users,DC=corp,DC=local";
            Assert.Equal(2, _dir.Get(u)!.GetAll("memberOf").Length);
            AssertNoChanges(src);

            // Someone adds the user to another group: declarad leaves that alone.
            _dir.SetAttribute("CN=Printers,CN=Users,DC=corp,DC=local", "member", u);
            AssertNoChanges(src);

            // Dropping VPN from the list removes only the membership declarad added.
            const string fewer = """
                group "team" {
                  name = "team"
                  members = []
                }
                user "u" {
                  sam_account_name = "u"
                  enabled = false
                  groups  = [group.team.dn]
                }
                """;
            var change = Plan(fewer).Changes.Single(c => c.Address == "user.u").Get("groups")!;
            Assert.Equal(new[] { "CN=VPN,CN=Users,DC=corp,DC=local" }, change.Removed);
            Apply(fewer);
            var memberOf = _dir.Get(u)!.GetAll("memberOf");
            Assert.DoesNotContain("CN=VPN,CN=Users,DC=corp,DC=local", memberOf);
            Assert.Contains("CN=Printers,CN=Users,DC=corp,DC=local", memberOf);
            AssertNoChanges(fewer);
        }

        [Fact]
        public void GroupMembersAreAuthoritative()
        {
            _dir.Seed("CN=intruder,CN=Users,DC=corp,DC=local", "user", ("sAMAccountName", "intruder"));
            const string src = """
                user "a" {
                  sam_account_name = "a"
                  enabled = false
                }
                group "g" {
                  name = "g"
                  members = [user.a.dn]
                }
                """;
            Apply(src);
            _dir.SetAttribute("CN=g,CN=Users,DC=corp,DC=local", "member", "CN=intruder,CN=Users,DC=corp,DC=local");
            var change = Plan(src).Changes.Single(c => c.Address == "group.g").Get("members")!;
            Assert.Equal(new[] { "CN=a,CN=Users,DC=corp,DC=local" }, change.Added);
            Assert.Equal(new[] { "CN=intruder,CN=Users,DC=corp,DC=local" }, change.Removed);
            Apply(src);
            Assert.Equal(new[] { "CN=a,CN=Users,DC=corp,DC=local" }, _dir.Get("CN=g,CN=Users,DC=corp,DC=local")!.GetAll("member"));
        }

        [Fact]
        public void ChangesGroupScopeThroughUniversal()
        {
            Apply("group \"g\" {\n  name = \"g\"\n  scope = \"global\"\n}");
            Apply("group \"g\" {\n  name = \"g\"\n  scope = \"domain_local\"\n}");
            Assert.Equal(GroupResource.GroupType("domain_local", "security").ToString(), _dir.Attr("CN=g,CN=Users,DC=corp,DC=local", "groupType"));
            AssertNoChanges("group \"g\" {\n  name = \"g\"\n  scope = \"domain_local\"\n}");
        }

        [Fact]
        public void PlanFailsForUnresolvableMembersAndUnsafeCreates()
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("group \"g\" {\n  name = \"g\"\n  members = [\"ghost\"]\n}"));
            Assert.Contains("ghost", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);

            ex = Assert.ThrowsAny<DeclaradException>(() => Plan("user \"u\" {\n  sam_account_name = \"u\"\n}"));
            Assert.Contains("initial_password", ex is ConfigErrorsException c2 ? c2.Errors[0].Message : ex.Message);

            ex = Assert.ThrowsAny<DeclaradException>(() => Plan("user \"u\" {\n  sam_account_name = \"u\"\n  initial_password = \"x\"\n}", new PlanOptions { Encrypted = false }));
            Assert.Contains("encrypted", ex is ConfigErrorsException c3 ? c3.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void ExportedConfigurationIsAdoptedWithoutChanges()
        {
            Apply(Basic);
            _dir.Seed("OU=Unmanaged,DC=corp,DC=local", "organizationalUnit", ("description", "made by hand"));
            string hcl = new Exporter(_dir).Export(null, Exporter.DefaultTypes.ToList());

            Assert.Contains("path = ou.staff.dn", hcl);
            Assert.Contains("user.jdoe.dn", hcl);
            Assert.DoesNotContain("Domain Admins\" {", hcl);

            // A fresh state with the exported config adopts everything and finds nothing to change.
            _state = new StateFile();
            var config = ConfigLoader.LoadSources(".", new[] { ("c.dad", ConfigTests.Connection), ("export.dad", hcl) });
            var plan = new Planner(_dir, config, _state, new PlanOptions { Adopt = true }).Build();
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(6, plan.Changes.Count(c => c.Adopt));
        }

        [Fact]
        public void StateRoundTripsThroughJson()
        {
            Apply(Basic);
            var copy = StateFile.FromJson(System.Text.Json.Nodes.JsonNode.Parse(_state.ToJson().ToJsonString())!);
            Assert.Equal(_state.Resources.Select(r => (r.Address, r.Guid, r.Dn)), copy.Resources.Select(r => (r.Address, r.Guid, r.Dn)));
            Assert.Equal(_state.Find("group.devs")!.Get("members"), copy.Find("group.devs")!.Get("members"));
            Assert.IsType<ListValue>(copy.Find("user.jdoe")!.Get("groups"));
        }
    }
}
