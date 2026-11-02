using Declarad.Cli;
using Declarad.Config;
using Declarad.Engine;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>Managed objects deleted outside declarad are restored from the AD Recycle Bin instead of created again.</summary>
    public class RecycleBinTests
    {
        private const string Alice = "CN=Alice Example,OU=lab,DC=corp,DC=local";
        private const string Team = "CN=team,OU=lab,DC=corp,DC=local";
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null, Connect = _ => _dir });

        private Plan Plan(string src, bool restore = true) => new Planner(_dir, Load(src), _state, new PlanOptions { Restore = restore }).Build();

        private ApplyResult Apply(string src, bool restore = true)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(src, restore));
            Assert.True(result.Success, string.Join("\n", result.Errors));
            return result;
        }

        private void AssertConverged(string src) =>
            Assert.Empty(Plan(src).Changes.Where(c => c.Action != ChangeAction.NoOp).Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old}->{x.New}"))}"));

        private static string Config(string title = "Engineer") => $$"""
            ou "lab" {
              protected_from_accidental_deletion = false
            }
            group "team" {
              path = ou.lab.dn
            }
            user "alice" {
              given_name       = "Alice"
              surname          = "Example"
              title            = "{{title}}"
              path             = ou.lab.dn
              initial_password = "P@ssw0rd!"
              groups           = [group.team.dn]
            }
            """;

        [Fact]
        public void RestoresAUserDeletedOutsideDeclarad()
        {
            Apply(Config());
            var before = _dir.Get(Alice)!;
            _dir.DeleteToRecycleBin(Alice);

            var plan = Plan(Config());
            var change = Assert.Single(plan.Changes, c => c.Action != ChangeAction.NoOp);
            Assert.Equal(ChangeAction.Restore, change.Action);
            Assert.Contains("AD Recycle Bin", Assert.Single(change.Notes));
            var ui = new ConsoleUi();
            ui.DisableColor();
            string text = new PlanRenderer(ui).Render(plan);
            Assert.Contains("# user.alice will be restored from the AD Recycle Bin", text);
            Assert.Contains("Plan: 0 to add, 0 to change, 0 to destroy, 1 to restore.", text);

            var result = Apply(Config());
            Assert.Equal(1, result.Restored);
            Assert.Equal(0, result.Created);
            var after = _dir.Get(Alice)!;
            Assert.Equal(before.Guid, after.Guid);
            Assert.Equal(before.Sid, after.Sid);
            Assert.Equal("Engineer", after.Get("title"));
            Assert.Contains(Alice, _dir.Get(Team)!.GetAll("member"));
            AssertConverged(Config());
        }

        [Fact]
        public void AppliesConfigurationChangesAfterTheRestore()
        {
            Apply(Config());
            var guid = _dir.Get(Alice)!.Guid;
            _dir.DeleteToRecycleBin(Alice);

            var change = Assert.Single(Plan(Config("Lead")).Changes, c => c.Action != ChangeAction.NoOp);
            Assert.Equal(ChangeAction.Restore, change.Action);
            Assert.Equal("title", Assert.Single(change.Changes).Name);

            Apply(Config("Lead"));
            Assert.Equal(guid, _dir.Get(Alice)!.Guid);
            Assert.Equal("Lead", _dir.Attr(Alice, "title"));
            AssertConverged(Config("Lead"));
        }

        [Fact]
        public void RestoresAContainerBeforeTheObjectsInIt()
        {
            Apply(Config());
            var ou = _dir.Get("OU=lab,DC=corp,DC=local")!.Guid;
            var alice = _dir.Get(Alice)!.Guid;
            _dir.DeleteToRecycleBin(Alice);
            _dir.DeleteToRecycleBin(Team);
            _dir.DeleteToRecycleBin("OU=lab,DC=corp,DC=local");

            var plan = Plan(Config());
            var order = plan.Changes.Where(c => c.Action == ChangeAction.Restore).Select(c => c.Address).ToList();
            Assert.Equal(3, order.Count);
            Assert.True(order.IndexOf("ou.lab") < order.IndexOf("user.alice"));

            Apply(Config());
            Assert.Equal(ou, _dir.Get("OU=lab,DC=corp,DC=local")!.Guid);
            Assert.Equal(alice, _dir.Get(Alice)!.Guid);
            Assert.Contains(Alice, _dir.Get(Team)!.GetAll("member"));
            AssertConverged(Config());
        }

        [Fact]
        public void CreatesTheObjectAgainWhenItIsNotInTheRecycleBin()
        {
            Apply(Config());
            var guid = _dir.Get(Alice)!.Guid;
            _dir.DeleteOutOfBand(Alice);

            var change = Assert.Single(Plan(Config()).Changes, c => c.Action != ChangeAction.NoOp);
            Assert.Equal(ChangeAction.Create, change.Action);
            Assert.Contains("new SID", Assert.Single(change.Notes));
            Apply(Config());
            Assert.NotEqual(guid, _dir.Get(Alice)!.Guid);
        }

        [Fact]
        public void CreatesTheObjectAgainWhenTheRecycleBinIsDisabled()
        {
            Apply(Config());
            _dir.DeleteToRecycleBin(Alice);
            _dir.RecycleBinEnabled = false;

            Assert.Equal(ChangeAction.Create, Assert.Single(Plan(Config()).Changes, c => c.Action != ChangeAction.NoOp).Action);
        }

        [Fact]
        public void NoRestoreCreatesANewObject()
        {
            Apply(Config());
            _dir.DeleteToRecycleBin(Alice);

            Assert.Equal(ChangeAction.Create, Assert.Single(Plan(Config(), restore: false).Changes, c => c.Action != ChangeAction.NoOp).Action);
        }

        [Fact]
        public void RefusesToRestoreOntoADnTakenByAnotherObject()
        {
            Apply(Config());
            _dir.DeleteToRecycleBin(Alice);
            _dir.Add(Alice, new Dictionary<string, object[]> { ["objectClass"] = new object[] { "contact" } });

            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan(Config()));
            Assert.Contains("is now taken by another object", ex.Message);
            Assert.Contains("--no-restore", ex.Message);
        }
    }
}
