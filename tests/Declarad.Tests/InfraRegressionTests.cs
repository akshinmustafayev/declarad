using Declarad.Cli;
using Declarad.Directory;
using Declarad.State;
using Declarad.State.Backends;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>Regression tests for bugs found in review of state, CLI and directory helpers.</summary>
    public class InfraRegressionTests
    {
        private static string NewProject(string extra = "")
        {
            string dir = System.IO.Directory.CreateTempSubdirectory("dad-infra").FullName;
            File.WriteAllText(Path.Combine(dir, "main.dad"), ConfigTests.Connection + "group \"g\" {\n  name = \"g\"\n}\n" + extra);
            return dir;
        }

        private static Commands Cli() => new(NullLoggerFactory.Instance);

        private static StateFile State(string address = "group.g")
        {
            var state = new StateFile();
            state.Upsert(new StateResource { Address = address, Type = "group", Guid = Guid.NewGuid(), Dn = "CN=g,DC=corp,DC=local" });
            return state;
        }

        [Fact]
        public void BackendConfigGivenToInitIsRemembered()
        {
            string dir = NewProject("declarad {\n  backend \"local\" {\n    path = \"a.json\"\n  }\n}\n");
            Assert.Equal(0, Cli().Init(new GlobalOptions { Dir = dir, BackendConfig = new[] { "path=b.json" } }));
            Assert.Equal(0, Cli().Output(new GlobalOptions { Dir = dir }));
            StateStore.Save(Path.Combine(dir, "b.json"), State());
            Assert.Equal(0, new StateCommands(NullLoggerFactory.Instance).Remove(new GlobalOptions { Dir = dir }, new[] { "group.g" }));
            Assert.Empty(StateStore.Load(Path.Combine(dir, "b.json")).Resources);

            // Changing the block itself still requires init.
            File.WriteAllText(Path.Combine(dir, "main.dad"), File.ReadAllText(Path.Combine(dir, "main.dad")).Replace("a.json", "c.json"));
            Assert.Equal(1, Cli().Output(new GlobalOptions { Dir = dir }));
        }

        [Fact]
        public void MigrationReportsTheResultAndRetiresTheOldLocalFile()
        {
            string dir = NewProject();
            StateStore.Save(Path.Combine(dir, StateStore.DefaultFileName), State());
            Assert.Equal(0, Cli().Init(new GlobalOptions { Dir = dir }));
            File.AppendAllText(Path.Combine(dir, "main.dad"), "declarad {\n  backend \"local\" {\n    path = \"moved.json\"\n  }\n}\n");
            Assert.Equal(0, Cli().Init(new GlobalOptions { Dir = dir }, migrateState: true));
            Assert.False(File.Exists(Path.Combine(dir, StateStore.DefaultFileName)));
            Assert.True(File.Exists(Path.Combine(dir, StateStore.DefaultFileName + ".migrated")));
            Assert.Single(StateStore.Load(Path.Combine(dir, "moved.json")).Resources);
        }

        /// <summary>A backend without locking (http without lock_address) must keep the conflict check.</summary>
        private sealed class NoLockBackend : IStateBackend
        {
            public StateFile? Stored;
            public string Type => "test";
            public string Location => "memory";
            public StateFile? Read() => Stored == null ? null : StateFile.FromJson(Stored.ToJson());
            public void Write(StateFile state) => Stored = StateFile.FromJson(state.ToJson());
            public bool Lock(LockInfo info) => false;
            public void Unlock(string lockId) => throw new InvalidOperationException("nothing was locked");
            public void ForceUnlock(string lockId) => throw new InvalidOperationException();
        }

        [Fact]
        public void BackendsWithoutLockingKeepTheConflictCheck()
        {
            var backend = new NoLockBackend();
            var first = new StateManager(backend);
            var firstState = first.Load();
            var second = new StateManager(backend);
            var secondState = second.Load();
            using (first.Lock("apply", TimeSpan.Zero))
            {
                first.Save(firstState);
            }
            using (second.Lock("apply", TimeSpan.Zero))
            {
                // Refused (here because the second run loaded before any state existed, so its lineage differs).
                Assert.Throws<DeclaradException>(() => second.Save(secondState));
            }
        }

        [Fact]
        public void StatePushBumpsTheSerial()
        {
            string dir = NewProject();
            string statePath = Path.Combine(dir, StateStore.DefaultFileName);
            var state = State();
            StateStore.Save(statePath, state);
            File.WriteAllText(Path.Combine(dir, "edited.json"), File.ReadAllText(statePath));
            Assert.Equal(0, new StateCommands(NullLoggerFactory.Instance).Push(new GlobalOptions { Dir = dir }, "edited.json"));
            Assert.Equal(state.Serial + 1, StateStore.Load(statePath).Serial);
            Assert.Equal(1, new StateCommands(NullLoggerFactory.Instance).Push(new GlobalOptions { Dir = dir }, "missing.json"));
        }

        [Theory]
        [InlineData("{\"version\":1,\"resources\":[{\"type\":\"group\"}]}")]
        [InlineData("{\"version\":1,\"resources\":[null]}")]
        [InlineData("{\"version\":1,\"outputs\":{\"x\":1e400},\"resources\":[]}")]
        public void MalformedStateIsReportedAsCorrupt(string json)
        {
            string message = Assert.Throws<DeclaradException>(() => StateStore.Parse(json, "test")).Message;
            Assert.True(message.Contains("corrupt") || message.Contains("out of range"), message);
        }

        [Fact]
        public void OddLockFilesAndTimeoutsAreHandled()
        {
            string dir = NewProject();
            Assert.Equal(0, Cli().Init(new GlobalOptions { Dir = dir }));
            File.WriteAllText(Path.Combine(dir, StateStore.DefaultFileName + ".lock.info"), "{\"ID\":5}");
            Assert.Equal(1, new StateCommands(NullLoggerFactory.Instance).Remove(new GlobalOptions { Dir = dir }, new[] { "group.g" }));
            Assert.Null(LockInfo.Parse("[]"));
            File.Delete(Path.Combine(dir, StateStore.DefaultFileName + ".lock.info"));
            foreach (string timeout in new[] { "NaN", "1e20s", "-5s" })
            {
                Assert.Equal(1, new StateCommands(NullLoggerFactory.Instance).Remove(new GlobalOptions { Dir = dir, LockTimeout = timeout }, new[] { "group.g" }));
            }
        }

        [Fact]
        public void RelativePathsAreRelativeToTheConfigurationDirectory()
        {
            string dir = NewProject();
            File.WriteAllText(Path.Combine(dir, "main.dad"), File.ReadAllText(Path.Combine(dir, "main.dad")) + "\ngroup   \"h\"   {\nname=\"h\"\n}\n");
            Assert.Equal(3, Cli().Fmt(new GlobalOptions { Dir = dir }, new[] { "main.dad" }, check: true));
            Assert.Equal(0, Cli().Fmt(new GlobalOptions { Dir = dir }, new[] { "main.dad" }));
        }

        [Theory]
        [InlineData("S-1-281474976710656-1")]
        [InlineData("S-1-5-abc")]
        [InlineData("S-1-5-")]
        [InlineData("S-300-5-1")]
        public void RejectsInvalidSids(string sid) => Assert.Throws<DeclaradException>(() => Sid.ToBytes(sid));

        [Fact]
        public void HandlesHexSidAuthorities()
        {
            var bytes = Sid.ToBytes("S-1-0x123456789ABC-7");
            Assert.Equal("S-1-0x123456789ABC-7", Sid.ToString(bytes));
            Assert.Equal("S-1-5-21-1-2-3", Sid.ToString(Sid.ToBytes("S-1-5-21-1-2-3")));
        }

        [Fact]
        public void RefusesToProtectObjectsWithoutADacl()
        {
            var noDacl = InMemoryDirectory.DefaultSd()[..20].ToArray();
            Array.Clear(noDacl, 4, 16);
            Assert.Throws<DeclaradException>(() => SecurityDescriptor.SetProtectedFromDeletion(noDacl, true));
            Assert.Throws<DeclaradException>(() => SecurityDescriptor.IsProtectedFromDeletion(InMemoryDirectory.DefaultSd()[..30]));
        }
    }
}
