using Declarad.Config;
using Declarad.Engine;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>Renaming a block label keeps the object, as long as the block still describes the same object.</summary>
    public class LabelRenameTests
    {
        private const string DomainAdmins = "CN=Domain Admins,CN=Users,DC=corp,DC=local";
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src, IReadOnlyDictionary<string, string>? labels = null) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null, Connect = _ => _dir, LabelDefaults = labels });

        // As the CLI does: load, then load again with the old labels of renamed blocks.
        private Plan Plan(string src) =>
            new Planner(_dir, LabelRenames.Resolve(Load(src), _state, labels => Load(src, labels)), _state, new PlanOptions()).Build();

        private void Apply(string src)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(src));
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }

        private void AssertConverged(string src) =>
            Assert.DoesNotContain(Plan(src).Changes, c => c.Action != ChangeAction.NoOp || c.Adopt);

        [Fact]
        public void RenamedUserLabelKeepsTheObjectAndReferences()
        {
            Apply("""
                user "alice" {
                  sam_account_name = "alice"
                  given_name       = "Alice"
                  surname          = "Example"
                  initial_password = "P@ssw0rd!"
                }
                group "lab" { members = [user.alice.dn] }
                """);
            var guid = _state.Find("user.alice")!.Guid;
            const string renamed = """
                user "alice_example" {
                  sam_account_name = "alice"
                  given_name       = "Alice"
                  surname          = "Example"
                  initial_password = "P@ssw0rd!"
                }
                group "lab" { members = [user.alice_example.dn] }
                """;

            var plan = Plan(renamed);
            var change = Assert.Single(plan.Changes, c => c.Adopt);
            Assert.Equal("user.alice_example", change.Address);
            Assert.Equal(ChangeAction.NoOp, change.Action);
            Assert.Contains("moved from user.alice", change.Notes);
            Assert.DoesNotContain(plan.Changes, c => c.Action is ChangeAction.Delete or ChangeAction.Create);
            Assert.Empty(plan.Warnings);

            Apply(renamed);
            Assert.Null(_state.Find("user.alice"));
            Assert.Equal(guid, _state.Find("user.alice_example")!.Guid);
            AssertConverged(renamed);
        }

        [Fact]
        public void RenamedGroupLabelKeepsItsNameAndMembers()
        {
            // The group's CN comes from the label; renaming the block must not rename the group.
            Apply("""
                group "admins" { sam_account_name = "lab-admins" }
                user "bob" {
                  enabled = false
                  groups  = [group.admins.dn]
                }
                """);
            var guid = _state.Find("group.admins")!.Guid;
            const string renamed = """
                group "lab_admins" { sam_account_name = "lab-admins" }
                user "bob" {
                  enabled = false
                  groups  = [group.lab_admins.dn]
                }
                """;

            var change = Assert.Single(Plan(renamed).Changes, c => c.Address == "group.lab_admins");
            Assert.Equal(ChangeAction.NoOp, change.Action);
            Assert.Contains("moved from group.admins", change.Notes);

            Apply(renamed);
            Assert.Equal(guid, _dir.Get("CN=admins,CN=Users,DC=corp,DC=local")!.Guid);
            Assert.Contains("CN=bob,CN=Users,DC=corp,DC=local", _dir.Get("CN=admins,CN=Users,DC=corp,DC=local")!.GetAll("member"));
            AssertConverged(renamed);
        }

        [Fact]
        public void ChangingTheNameOfARenamedGroupKeepsItsMembers()
        {
            // A CN change moves the group's DN; users listing it in groups keep their membership.
            Apply("""
                group "admins" { sam_account_name = "lab-admins" }
                user "bob" {
                  enabled = false
                  groups  = [group.admins.dn]
                }
                """);
            const string renamed = """
                group "admins" {
                  name             = "Lab Admins"
                  sam_account_name = "lab-admins"
                }
                user "bob" {
                  enabled = false
                  groups  = [group.admins.dn]
                }
                """;
            Apply(renamed);
            Assert.Contains("CN=bob,CN=Users,DC=corp,DC=local", _dir.Get("CN=Lab Admins,CN=Users,DC=corp,DC=local")!.GetAll("member"));
            AssertConverged(renamed);
        }

        [Fact]
        public void RenamedLabelIsRecognisedEvenWhenTheRecordedDnIsOutOfDate()
        {
            Apply("""
                user "carol" {
                  name    = "Carol"
                  enabled = false
                }
                """);
            _state.Find("user.carol")!.Dn = "CN=Somewhere Else,CN=Users,DC=corp,DC=local";
            const string renamed = """
                user "carol2" {
                  name             = "Carol"
                  enabled          = false
                  sam_account_name = "carol"
                }
                """;

            var change = Assert.Single(Plan(renamed).Changes);
            Assert.Equal("user.carol2", change.Address);
            Assert.Contains("moved from user.carol", change.Notes);
            Apply(renamed);
            AssertConverged(renamed);
        }

        [Fact]
        public void RenamedAttachmentLabelDoesNotDetachWhatItAttached()
        {
            Apply("""
                user "ops" { enabled = false }
                group_membership "da" {
                  group   = "Domain Admins"
                  members = [user.ops.dn]
                }
                """);
            const string renamed = """
                user "ops" { enabled = false }
                group_membership "domain_admins" {
                  group   = "Domain Admins"
                  members = [user.ops.dn]
                }
                """;

            var plan = Plan(renamed);
            Assert.DoesNotContain(plan.Changes, c => c.Address == "group_membership.da");
            Assert.Contains("moved from group_membership.da", Assert.Single(plan.Changes, c => c.Address == "group_membership.domain_admins").Notes);

            Apply(renamed);
            Assert.Contains("CN=ops,CN=Users,DC=corp,DC=local", _dir.Get(DomainAdmins)!.GetAll("member"));
            AssertConverged(renamed);

            // The renamed attachment still owns the membership it added, so removing it detaches the member.
            Apply("""user "ops" { enabled = false }""");
            Assert.DoesNotContain("CN=ops,CN=Users,DC=corp,DC=local", _dir.Get(DomainAdmins)!.GetAll("member"));
        }

        private const string Alice = "CN=alice,CN=Users,DC=corp,DC=local";

        [Fact]
        public void RenamingALabelThatIsAlsoTheLogonNameKeepsTheAccountAndItsValues()
        {
            // sam_account_name, the CN and the UPN all come from the label.
            Apply("""
                user "alice" { initial_password = "P@ssw0rd!" }
                group "lab" { members = [user.alice.dn] }
                """);
            var guid = _dir.Get(Alice)!.Guid;
            const string renamed = """
                user "alice2" { initial_password = "P@ssw0rd!" }
                group "lab" { members = [user.alice2.dn] }
                """;

            var plan = Plan(renamed);
            var change = Assert.Single(plan.Changes, c => c.Adopt);
            Assert.Equal("user.alice2", change.Address);
            Assert.Equal(ChangeAction.NoOp, change.Action);
            Assert.Contains("moved from user.alice", change.Notes);
            Assert.DoesNotContain(plan.Changes, c => c.Action != ChangeAction.NoOp);
            Assert.Empty(plan.Warnings);
            var ui = new Cli.ConsoleUi();
            ui.DisableColor();
            string text = new Cli.PlanRenderer(ui).Render(plan);
            Assert.Contains("# user.alice has moved to user.alice2\n    user.alice2 {\n        # dn = \"CN=alice,CN=Users,DC=corp,DC=local\"\n    }", text.Replace("\r\n", "\n"));
            Assert.Contains("Plan: 0 to add, 0 to change, 0 to destroy.", text);

            int writes = _dir.WriteCount;
            Apply(renamed);
            Assert.Equal(writes, _dir.WriteCount);
            var user = _dir.Get(Alice)!;
            Assert.Equal(guid, user.Guid);
            Assert.Equal("alice", user.Get("sAMAccountName"));
            Assert.Equal("alice@corp.local", user.Get("userPrincipalName"));
            Assert.Equal("alice", _state.Find("user.alice2")!.LabelDefault);
            Assert.Contains("\"label_default\": \"alice\"", _state.ToJson().ToString());
            AssertConverged(renamed);

            // Renamed again: still the same account.
            const string again = """
                user "alice_example" { initial_password = "P@ssw0rd!" }
                group "lab" { members = [user.alice_example.dn] }
                """;
            Apply(again);
            Assert.Equal(guid, _dir.Get(Alice)!.Guid);
            Assert.Equal("alice", _state.Find("user.alice_example")!.LabelDefault);
            AssertConverged(again);

            // Back to the original label: nothing to remember any more.
            Apply("""
                user "alice" { initial_password = "P@ssw0rd!" }
                group "lab" { members = [user.alice.dn] }
                """);
            Assert.Equal(guid, _dir.Get(Alice)!.Guid);
            Assert.Null(_state.Find("user.alice")!.LabelDefault);
        }

        [Fact]
        public void SettingTheLogonNameAfterARenameChangesItOnTheSameAccount()
        {
            Apply("""user "alice" { initial_password = "P@ssw0rd!" }""");
            var guid = _dir.Get(Alice)!.Guid;
            Apply("""user "alice2" { initial_password = "P@ssw0rd!" }""");

            const string explicitName = """
                user "alice2" {
                  sam_account_name = "alice2"
                  initial_password = "P@ssw0rd!"
                }
                """;
            var change = Assert.Single(Plan(explicitName).Changes);
            Assert.Equal(ChangeAction.Update, change.Action);
            Assert.Contains(change.Changes, c => c.Name == "sam_account_name");
            Apply(explicitName);
            Assert.Equal(guid, _dir.Get("CN=alice2,CN=Users,DC=corp,DC=local")!.Guid);
            AssertConverged(explicitName);
        }

        [Fact]
        public void SeveralRenamesInOneApplyKeepEveryObject()
        {
            Apply("""
                ou "lab" {
                  protected_from_accidental_deletion = false
                }
                user "alice" {
                  path    = ou.lab.dn
                  enabled = false
                  title   = "Engineer"
                }
                user "bob" {
                  path    = ou.lab.dn
                  enabled = false
                  title   = "Accountant"
                }
                """);
            var ou = _dir.Get("OU=lab,DC=corp,DC=local")!.Guid;
            var alice = _dir.Get("CN=alice,OU=lab,DC=corp,DC=local")!.Guid;
            var bob = _dir.Get("CN=bob,OU=lab,DC=corp,DC=local")!.Guid;
            const string renamed = """
                ou "playground" {
                  protected_from_accidental_deletion = false
                }
                user "alice_example" {
                  path    = ou.playground.dn
                  enabled = false
                  title   = "Engineer"
                }
                user "bob_example" {
                  path    = ou.playground.dn
                  enabled = false
                  title   = "Accountant"
                }
                """;

            Assert.DoesNotContain(Plan(renamed).Changes, c => c.Action != ChangeAction.NoOp);
            Apply(renamed);
            Assert.Equal(ou, _dir.Get("OU=lab,DC=corp,DC=local")!.Guid);
            Assert.Equal(alice, _dir.Get("CN=alice,OU=lab,DC=corp,DC=local")!.Guid);
            Assert.Equal(bob, _dir.Get("CN=bob,OU=lab,DC=corp,DC=local")!.Guid);
            AssertConverged(renamed);
        }

        [Fact]
        public void MovedBlockKeepsLabelDerivedValues()
        {
            Apply("""user "alice" { enabled = false }""");
            var guid = _dir.Get(Alice)!.Guid;
            const string moved = """
                user "a" { enabled = false }
                moved {
                  from = user.alice
                  to   = user.a
                }
                """;
            Assert.DoesNotContain(Plan(moved).Changes, c => c.Action != ChangeAction.NoOp);
            Apply(moved);
            Assert.Equal(guid, _dir.Get(Alice)!.Guid);
            AssertConverged(moved);
        }

        [Fact]
        public void StateMvRemembersTheOldLabel()
        {
            Apply("""user "alice" { enabled = false }""");
            var s = _state.Find("user.alice")!;
            Assert.Equal("alice", LabelRenames.EffectiveLabel(s));
            Assert.Equal("alice", LabelRenames.Label("module.team[\"a.b\"].user.alice", "user"));
            Assert.Null(LabelRenames.Label("user.staff[\"alice\"]", "user"));
        }

        [Fact]
        public void RenameThatAlsoChangesTheNameDescribesANewObjectAndThePlanSaysSo()
        {
            Apply("""
                user "dave" {
                  given_name = "Dave"
                  surname    = "Old"
                  enabled    = false
                }
                """);
            var plan = Plan("""
                user "dave2" {
                  given_name = "Dave"
                  surname    = "New"
                  enabled    = false
                }
                """);

            Assert.Contains(plan.Changes, c => c.Address == "user.dave" && c.Action == ChangeAction.Delete);
            Assert.Contains(plan.Changes, c => c.Address == "user.dave2" && c.Action == ChangeAction.Create);
            var warning = Assert.Single(plan.Warnings);
            Assert.Contains("moved block with from = user.dave and to = user.dave2", warning);
        }

        /// <summary>Every resource type, with names taken from the labels wherever the type allows it.</summary>
        private static string AllTypes(string p) => $$"""
            data "gpo" "default" {
              name = "Default Domain Policy"
            }
            ou "{{p}}lab" {
              protected_from_accidental_deletion = false
            }
            container "{{p}}stuff" {
              path = ou.{{p}}lab.dn
            }
            group "{{p}}team" {
              path = ou.{{p}}lab.dn
            }
            user "{{p}}alice" {
              path    = ou.{{p}}lab.dn
              enabled = false
              groups  = [group.{{p}}team.dn]
            }
            computer "{{p}}ws01" {
              path = ou.{{p}}lab.dn
            }
            contact "{{p}}vendor" {
              path = ou.{{p}}lab.dn
            }
            service_account "{{p}}svcapp" {
              principals_allowed_to_retrieve_password = [group.{{p}}team.dn]
            }
            password_policy "{{p}}strict" {
              precedence = 10
              applies_to = [group.{{p}}team.dn]
            }
            group_membership "{{p}}da" {
              group   = "Domain Admins"
              members = [user.{{p}}alice.dn]
            }
            gpo_link "{{p}}default" {
              gpo    = data.gpo.default.id
              target = ou.{{p}}lab.dn
            }
            dns_zone "{{p}}zone" {
              name = "lab.example"
            }
            dns_record "{{p}}www" {
              zone    = dns_zone.{{p}}zone.name
              name    = "www"
              type    = "A"
              records = ["10.0.0.1"]
            }
            """;

        [Fact]
        public void RenamingEveryBlockChangesOnlyTheState()
        {
            Apply(AllTypes(""));
            var guids = _state.Resources.ToDictionary(r => r.Address.Split('.')[0] + "." + r.Address.Split('.')[1], r => r.Guid);
            int writes = _dir.WriteCount;
            string renamed = AllTypes("new_");

            var plan = Plan(renamed);
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(12, plan.Changes.Count(c => c.Adopt));
            Assert.All(plan.Changes, c => Assert.StartsWith("moved from", Assert.Single(c.Notes)));
            Assert.Empty(plan.Warnings);

            Apply(renamed);
            Assert.Equal(writes, _dir.WriteCount);
            Assert.All(_state.Resources, r => Assert.Contains(".new_", r.Address));
            foreach (var (address, guid) in guids)
            {
                string type = address.Split('.')[0];
                Assert.Equal(guid, _state.Find(type + ".new_" + address.Split('.')[1])!.Guid);
            }
            AssertConverged(renamed);

            // And back again.
            Apply(AllTypes(""));
            Assert.Equal(writes, _dir.WriteCount);
            Assert.All(_state.Resources, r => Assert.Null(r.LabelDefault));
            AssertConverged(AllTypes(""));
        }

        [Fact]
        public void RenamingAForEachBlockChangesOnlyTheState()
        {
            static string Src(string label) => $$"""
                user "{{label}}" {
                  for_each         = toset(["alice", "bob"])
                  sam_account_name = each.key
                  enabled          = false
                }
                group "team" {
                  members = values(user.{{label}})[*].dn
                }
                """;
            Apply(Src("staff"));
            int writes = _dir.WriteCount;

            var plan = Plan(Src("people"));
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(2, plan.Changes.Count(c => c.Adopt));

            Apply(Src("people"));
            Assert.Equal(writes, _dir.WriteCount);
            Assert.NotNull(_state.Find("user.people[\"alice\"]"));
            AssertConverged(Src("people"));
        }

        [Fact]
        public void RenamingAModuleCallChangesOnlyTheState()
        {
            const string module = """
                group "team" {}
                user "lead" {
                  enabled = false
                  groups  = [group.team.dn]
                }
                """;
            string root = Path.GetFullPath("/virtual");
            Configuration LoadTree(string main, IReadOnlyDictionary<string, string>? labels) =>
                ConfigLoader.Load(root, new LoadOptions
                {
                    GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null,
                    Connect = _ => _dir,
                    LabelDefaults = labels,
                    ReadModule = dir => Path.GetFileName(dir) == "team"
                        ? new[] { ("main.dad", module) }
                        : new[] { ("main.dad", ConfigTests.Connection + main) },
                });
            void ApplyTree(string main)
            {
                var config = LabelRenames.Resolve(LoadTree(main, null), _state, labels => LoadTree(main, labels));
                var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance)
                    .Apply(new Planner(_dir, config, _state, new PlanOptions()).Build());
                Assert.True(result.Success, string.Join("\n", result.Errors));
            }

            ApplyTree("""
                module "eng" {
                  source = "./modules/team"
                }
                """);
            int writes = _dir.WriteCount;
            const string renamed = """
                module "engineering" {
                  source = "./modules/team"
                }
                """;
            var plan = new Planner(_dir, LoadTree(renamed, null), _state, new PlanOptions()).Build();
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(2, plan.Changes.Count(c => c.Adopt));

            ApplyTree(renamed);
            Assert.Equal(writes, _dir.WriteCount);
            Assert.NotNull(_state.Find("module.engineering.user.lead"));
            Assert.Null(_state.Find("module.eng.user.lead"));
        }

        [Fact]
        public void TwoBlocksForOneObjectAreStillAnError()
        {
            Apply("""
                user "erin" {
                  name    = "Erin"
                  enabled = false
                }
                """);
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("""
                user "erin" {
                  name    = "Erin"
                  enabled = false
                }
                user "erin_copy" {
                  name             = "Erin"
                  enabled          = false
                  sam_account_name = "erin_copy"
                }
                """));
            Assert.Contains("user.erin_copy has the same distinguished name", ex.Message);
        }
    }
}
