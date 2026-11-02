using Declarad.Config;
using Declarad.Engine;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>
    /// Block labels are only addresses, as in Terraform: renaming one moves the state entry and changes nothing in the
    /// directory, as long as the block still describes the same object.
    /// </summary>
    public class LabelRenameTests
    {
        private const string DomainAdmins = "CN=Domain Admins,CN=Users,DC=corp,DC=local";
        private const string Alice = "CN=alice,CN=Users,DC=corp,DC=local";
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null, Connect = _ => _dir });

        private Plan Plan(string src) => new Planner(_dir, Load(src), _state, new PlanOptions()).Build();

        private void Apply(string src)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(src));
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }

        private void AssertConverged(string src) =>
            Assert.DoesNotContain(Plan(src).Changes, c => c.Action != ChangeAction.NoOp || c.Adopt);

        [Fact]
        public void TheLabelIsNotAValue()
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load("""
                user "alice" {
                  enabled = false
                }
                """));
            Assert.Contains("user.alice: attribute 'sam_account_name' is required. The block label is only the resource's address in declarad", ex.Message);

            ex = Assert.ThrowsAny<DeclaradException>(() => Load("ou \"staff\" {}"));
            Assert.Contains("ou.staff: attribute 'name' is required.", ex.Message);
        }

        [Fact]
        public void RenamingABlockChangesOnlyTheState()
        {
            Apply("""
                user "alice" {
                  sam_account_name = "alice"
                  initial_password = "P@ssw0rd!"
                }
                group "lab" {
                  name    = "lab"
                  members = [user.alice.dn]
                }
                """);
            var guid = _dir.Get(Alice)!.Guid;
            const string renamed = """
                user "alice2" {
                  sam_account_name = "alice"
                  initial_password = "P@ssw0rd!"
                }
                group "lab" {
                  name    = "lab"
                  members = [user.alice2.dn]
                }
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
            Assert.Null(_state.Find("user.alice"));
            Assert.Equal(guid, _state.Find("user.alice2")!.Guid);
            Assert.Equal("alice", _dir.Attr(Alice, "sAMAccountName"));
            AssertConverged(renamed);
        }

        [Fact]
        public void ChangingTheLogonNameUpdatesTheSameAccount()
        {
            Apply("""
                user "alice" {
                  sam_account_name = "alice"
                  initial_password = "P@ssw0rd!"
                }
                """);
            var guid = _dir.Get(Alice)!.Guid;
            const string changed = """
                user "alice" {
                  sam_account_name = "alice2"
                  initial_password = "P@ssw0rd!"
                }
                """;
            var change = Assert.Single(Plan(changed).Changes);
            Assert.Equal(ChangeAction.Update, change.Action);
            Assert.Contains(change.Changes, c => c.Name == "sam_account_name");
            Apply(changed);
            Assert.Equal(guid, _dir.Get("CN=alice2,CN=Users,DC=corp,DC=local")!.Guid);
            AssertConverged(changed);
        }

        [Fact]
        public void RenamedGroupKeepsItsMembers()
        {
            Apply("""
                group "admins" {
                  name = "admins"
                }
                user "bob" {
                  sam_account_name = "bob"
                  enabled          = false
                  groups           = [group.admins.dn]
                }
                """);
            var guid = _state.Find("group.admins")!.Guid;
            const string renamed = """
                group "lab_admins" {
                  name = "admins"
                }
                user "bob" {
                  sam_account_name = "bob"
                  enabled          = false
                  groups           = [group.lab_admins.dn]
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
        public void ChangingAGroupNameKeepsItsMembers()
        {
            // A CN change moves the group's DN; users listing it in groups keep their membership.
            Apply("""
                group "admins" {
                  name             = "admins"
                  sam_account_name = "lab-admins"
                }
                user "bob" {
                  sam_account_name = "bob"
                  enabled          = false
                  groups           = [group.admins.dn]
                }
                """);
            const string renamed = """
                group "admins" {
                  name             = "Lab Admins"
                  sam_account_name = "lab-admins"
                }
                user "bob" {
                  sam_account_name = "bob"
                  enabled          = false
                  groups           = [group.admins.dn]
                }
                """;
            Apply(renamed);
            Assert.Contains("CN=bob,CN=Users,DC=corp,DC=local", _dir.Get("CN=Lab Admins,CN=Users,DC=corp,DC=local")!.GetAll("member"));
            AssertConverged(renamed);
        }

        [Fact]
        public void RenamedBlockIsRecognisedEvenWhenTheRecordedDnIsOutOfDate()
        {
            Apply("""
                user "carol" {
                  sam_account_name = "carol"
                  name             = "Carol"
                  enabled          = false
                }
                """);
            _state.Find("user.carol")!.Dn = "CN=Somewhere Else,CN=Users,DC=corp,DC=local";
            const string renamed = """
                user "carol2" {
                  sam_account_name = "carol"
                  name             = "Carol"
                  enabled          = false
                }
                """;

            var change = Assert.Single(Plan(renamed).Changes);
            Assert.Equal("user.carol2", change.Address);
            Assert.Contains("moved from user.carol", change.Notes);
            Apply(renamed);
            AssertConverged(renamed);
        }

        [Fact]
        public void RenamedAttachmentDoesNotDetachWhatItAttached()
        {
            const string user = """
                user "ops" {
                  sam_account_name = "ops"
                  enabled          = false
                }

                """;
            Apply(user + """
                group_membership "da" {
                  group   = "Domain Admins"
                  members = [user.ops.dn]
                }
                """);
            string renamed = user + """
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
            Apply(user);
            Assert.DoesNotContain("CN=ops,CN=Users,DC=corp,DC=local", _dir.Get(DomainAdmins)!.GetAll("member"));
        }

        private const string TwoOus = """
            ou "a" {
              name                               = "a"
              protected_from_accidental_deletion = false
            }
            ou "b" {
              name                               = "b"
              protected_from_accidental_deletion = false
            }

            """;

        [Fact]
        public void RenamingABlockAndMovingTheObjectToAnotherOuMovesTheSameObject()
        {
            string before = TwoOus + """
                user "alice" {
                  sam_account_name = "alice"
                  given_name       = "Alice"
                  surname          = "Example"
                  path             = ou.a.dn
                  enabled          = false
                }
                group "team" {
                  name = "team"
                  path = ou.a.dn
                }
                """;
            Apply(before);
            var alice = _dir.Get("CN=Alice Example,OU=a,DC=corp,DC=local")!.Guid;
            var team = _dir.Get("CN=team,OU=a,DC=corp,DC=local")!.Guid;
            string after = before
                .Replace("user \"alice\"", "user \"alice_example\"")
                .Replace("group \"team\"", "group \"engineering\"")
                .Replace("ou.a.dn", "ou.b.dn");

            var plan = Plan(after);
            Assert.All(plan.Changes.Where(c => c.Action != ChangeAction.NoOp), c => Assert.Equal(ChangeAction.Update, c.Action));
            var change = Assert.Single(plan.Changes, c => c.Address == "user.alice_example");
            Assert.Equal("path", Assert.Single(change.Changes).Name);
            Assert.Contains("moved from user.alice", change.Notes);
            Assert.Contains("moved from group.team", Assert.Single(plan.Changes, c => c.Address == "group.engineering").Notes);
            Assert.Empty(plan.Warnings);

            Apply(after);
            Assert.Equal(alice, _dir.Get("CN=Alice Example,OU=b,DC=corp,DC=local")!.Guid);
            Assert.Equal(team, _dir.Get("CN=team,OU=b,DC=corp,DC=local")!.Guid);
            AssertConverged(after);
        }

        [Fact]
        public void RenamingABlockAndTheObjectNameKeepsTheAccount()
        {
            // Dave's surname changes and the block is renamed: the CN changes, the logon name does not.
            Apply("""
                user "dave" {
                  sam_account_name = "dave"
                  given_name       = "Dave"
                  surname          = "Old"
                  enabled          = false
                }
                """);
            var guid = _dir.Get("CN=Dave Old,CN=Users,DC=corp,DC=local")!.Guid;
            const string renamed = """
                user "dave_new" {
                  sam_account_name = "dave"
                  given_name       = "Dave"
                  surname          = "New"
                  enabled          = false
                }
                """;

            var change = Assert.Single(Plan(renamed).Changes);
            Assert.Equal(ChangeAction.Update, change.Action);
            Apply(renamed);
            Assert.Equal(guid, _dir.Get("CN=Dave New,CN=Users,DC=corp,DC=local")!.Guid);
            AssertConverged(renamed);
        }

        [Fact]
        public void ABlockWithADifferentNameAndLogonNameIsANewObjectAndThePlanSaysSo()
        {
            Apply("""
                user "dave" {
                  sam_account_name = "dave"
                  given_name       = "Dave"
                  surname          = "Old"
                  enabled          = false
                }
                """);
            var plan = Plan("""
                user "carol" {
                  sam_account_name = "carol"
                  given_name       = "Carol"
                  surname          = "New"
                  enabled          = false
                }
                """);

            Assert.Contains(plan.Changes, c => c.Address == "user.dave" && c.Action == ChangeAction.Delete);
            Assert.Contains(plan.Changes, c => c.Address == "user.carol" && c.Action == ChangeAction.Create);
            var warning = Assert.Single(plan.Warnings);
            Assert.Contains("name and logon name both differ", warning);
            Assert.Contains("moved block with from = user.dave and to = user.carol", warning);
        }

        [Fact]
        public void SeveralRenamesInOneApplyKeepEveryObject()
        {
            static string Src(string ou, string a, string b) => $$"""
                ou "{{ou}}" {
                  name                               = "lab"
                  protected_from_accidental_deletion = false
                }
                user "{{a}}" {
                  sam_account_name = "alice"
                  path             = ou.{{ou}}.dn
                  enabled          = false
                }
                user "{{b}}" {
                  sam_account_name = "bob"
                  path             = ou.{{ou}}.dn
                  enabled          = false
                }
                """;
            Apply(Src("lab", "alice", "bob"));
            int writes = _dir.WriteCount;

            Assert.DoesNotContain(Plan(Src("playground", "alice_example", "bob_example")).Changes, c => c.Action != ChangeAction.NoOp);
            Apply(Src("playground", "alice_example", "bob_example"));
            Assert.Equal(writes, _dir.WriteCount);
            Assert.NotNull(_state.Find("user.alice_example"));
            AssertConverged(Src("playground", "alice_example", "bob_example"));
        }

        /// <summary>Every resource type; <paramref name="p"/> prefixes only the labels, never the values.</summary>
        private static string AllTypes(string p) => $$"""
            data "gpo" "default" {
              name = "Default Domain Policy"
            }
            ou "{{p}}lab" {
              name                               = "lab"
              protected_from_accidental_deletion = false
            }
            container "{{p}}stuff" {
              name = "stuff"
              path = ou.{{p}}lab.dn
            }
            group "{{p}}team" {
              name = "team"
              path = ou.{{p}}lab.dn
            }
            user "{{p}}alice" {
              sam_account_name = "alice"
              path             = ou.{{p}}lab.dn
              enabled          = false
              groups           = [group.{{p}}team.dn]
            }
            computer "{{p}}ws01" {
              name = "ws01"
              path = ou.{{p}}lab.dn
            }
            contact "{{p}}vendor" {
              name = "vendor"
              path = ou.{{p}}lab.dn
            }
            service_account "{{p}}svcapp" {
              name                                    = "svcapp"
              principals_allowed_to_retrieve_password = [group.{{p}}team.dn]
            }
            password_policy "{{p}}strict" {
              name       = "strict"
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
            var guids = _state.Resources.ToDictionary(r => r.Address, r => r.Guid);
            int writes = _dir.WriteCount;
            string renamed = AllTypes("new_");

            var plan = Plan(renamed);
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(12, plan.Changes.Count(c => c.Adopt));
            Assert.All(plan.Changes, c => Assert.StartsWith("moved from", Assert.Single(c.Notes)));
            Assert.Empty(plan.Warnings);

            Apply(renamed);
            Assert.Equal(writes, _dir.WriteCount);
            foreach (var (address, guid) in guids)
            {
                var parts = address.Split('.');
                Assert.Equal(guid, _state.Find($"{parts[0]}.new_{parts[1]}")!.Guid);
            }
            AssertConverged(renamed);

            // And back again.
            Apply(AllTypes(""));
            Assert.Equal(writes, _dir.WriteCount);
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
                  name    = "team"
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
                group "team" {
                  name = "team"
                }
                user "lead" {
                  sam_account_name = "lead"
                  enabled          = false
                  groups           = [group.team.dn]
                }
                """;
            string root = Path.GetFullPath("/virtual");
            Configuration LoadTree(string main) =>
                ConfigLoader.Load(root, new LoadOptions
                {
                    GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null,
                    Connect = _ => _dir,
                    ReadModule = dir => Path.GetFileName(dir) == "team"
                        ? new[] { ("main.dad", module) }
                        : new[] { ("main.dad", ConfigTests.Connection + main) },
                });
            void ApplyTree(string main)
            {
                var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance)
                    .Apply(new Planner(_dir, LoadTree(main), _state, new PlanOptions()).Build());
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
            var plan = new Planner(_dir, LoadTree(renamed), _state, new PlanOptions()).Build();
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
                  sam_account_name = "erin"
                  name             = "Erin"
                  enabled          = false
                }
                """);
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("""
                user "erin" {
                  sam_account_name = "erin"
                  name             = "Erin"
                  enabled          = false
                }
                user "erin_copy" {
                  sam_account_name = "erin_copy"
                  name             = "Erin"
                  enabled          = false
                }
                """));
            Assert.Contains("user.erin_copy has the same distinguished name", ex.Message);
        }
    }
}
