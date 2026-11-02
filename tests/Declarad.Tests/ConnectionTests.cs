using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class ConnectionTests
    {
        private readonly InMemoryDirectory _hq = new();
        private readonly InMemoryDirectory _eu = new();
        private StateFile _state = new();

        private const string Connections = """
            connection {
              server   = "hq-dc"
              domain   = "corp.local"
              username = "u"
              password = "p"
            }
            connection "eu" {
              server   = "eu-dc"
              domain   = "corp.local"
              username = "u"
              password = "p"
            }

            """;

        private IDirectory Dir(string connection) => connection == "eu" ? _eu : _hq;

        private Configuration Load(Dictionary<string, string> files) =>
            ModuleTests.LoadTreeRaw(files.ToDictionary(kv => kv.Key, kv => kv.Key == "main.dad" ? Connections + kv.Value : kv.Value), settings => settings.Server == "eu-dc" ? _eu : _hq);

        private Plan Plan(Configuration config, PlanOptions? options = null) => new Planner(Dir, config, _state, options ?? new PlanOptions()).Build();

        private void Apply(Configuration config)
        {
            var result = new Applier(Dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(config));
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }

        [Fact]
        public void RoutesResourcesDataSourcesAndModulesToTheirConnections()
        {
            var files = new Dictionary<string, string>
            {
                ["main.dad"] = """
                    group "hq" {
                      name = "Staff"
                    }
                    group "eu" {
                      name       = "Staff"
                      connection = connection.eu
                    }
                    module "regional" {
                      source      = "./modules/regional"
                      connections = { default = connection.eu }
                    }
                    data "group" "eu_admins" {
                      connection       = connection.eu
                      sam_account_name = "Domain Admins"
                    }
                    output "eu_admins_sid" {
                      value = data.group.eu_admins.sid
                    }
                    output "eu_domain" {
                      value = connection.eu.base_dn
                    }
                    """,
                ["modules/regional/main.dad"] = """
                    ou "office" {
                      name                               = "Office"
                      protected_from_accidental_deletion = false
                    }
                    group "local_admins" {
                      name = "local_admins"
                      path = ou.office.dn
                    }
                    """,
            };
            var config = Load(files);
            Assert.Equal("eu", config.Find("group.eu")!.Connection);
            Assert.Equal("eu", config.Find("module.regional.ou.office")!.Connection);
            Assert.Equal("default", config.Find("group.hq")!.Connection);
            Assert.Equal(Value.From("DC=corp,DC=local"), config.Outputs.Single(o => o.Name == "eu_domain").Value);
            Assert.Contains("eu", config.OpenDirectories.Keys);

            Apply(config);
            Assert.True(_hq.Exists("CN=Staff,CN=Users,DC=corp,DC=local"));
            Assert.True(_eu.Exists("CN=Staff,CN=Users,DC=corp,DC=local"));
            Assert.True(_eu.Exists("CN=local_admins,OU=Office,DC=corp,DC=local"));
            Assert.False(_hq.Exists("OU=Office,DC=corp,DC=local"));
            Assert.Equal("eu", _state.Find("module.regional.group.local_admins")!.Connection);
            Assert.DoesNotContain(Plan(Load(files)).Changes, c => c.Action != ChangeAction.NoOp);

            // Objects cannot move between directories.
            files["main.dad"] = files["main.dad"].Replace("  name = \"Staff\"\n}\ngroup \"eu\"", "  name       = \"Staff HQ\"\n  connection = connection.eu\n}\ngroup \"eu\"");
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan(Load(files)));
            Assert.Contains("cannot move between directories", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void ReportsUnknownConnections()
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load(new() { ["main.dad"] = "group \"g\" {\n  name = \"g\"\n  connection = connection.apac\n}\n" }));
            Assert.Contains("Connection 'apac' is not available", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);

            ex = Assert.ThrowsAny<DeclaradException>(() => ModuleTests.LoadTreeRaw(new()
            {
                ["main.dad"] = "connection \"eu\" {\n  server = \"x\"\n  domain = \"corp.local\"\n}\ngroup \"g\" {}\n",
            }));
            Assert.Contains("no default connection", ex is ConfigErrorsException c2 ? c2.Errors[0].Message : ex.Message);
        }
    }
}
