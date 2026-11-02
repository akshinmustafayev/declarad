using Declarad.Config;
using Declarad.Hcl.Values;
using Declarad.Tests.Support;

namespace Declarad.Tests
{
    public class ModuleTests
    {
        /// <summary>Loads an in-memory multi-directory configuration (keys are "dir/file.dad") with the standard connection added.</summary>
        internal static Configuration LoadTree(Dictionary<string, string> files, Dictionary<string, string>? vars = null, Func<Declarad.Directory.ConnectionSettings, Declarad.Directory.IDirectory>? connect = null) =>
            LoadTreeRaw(new Dictionary<string, string>(files) { ["connection.dad"] = ConfigTests.Connection }, connect, vars);

        /// <summary>Loads an in-memory multi-directory configuration exactly as given.</summary>
        internal static Configuration LoadTreeRaw(Dictionary<string, string> files, Func<Declarad.Directory.ConnectionSettings, Declarad.Directory.IDirectory>? connect = null, Dictionary<string, string>? vars = null)
        {
            string root = Path.GetFullPath("/virtual");
            IReadOnlyList<(string, string)> Read(string dir)
            {
                string rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
                rel = rel == "." ? "" : rel + "/";
                return files.Where(f => f.Key.StartsWith(rel, StringComparison.Ordinal) && !f.Key[rel.Length..].Contains('/'))
                    .Select(f => (f.Key[rel.Length..], f.Value)).ToList();
            }
            return ConfigLoader.Load(root, new LoadOptions { ReadModule = Read, Vars = vars, GetEnv = _ => null, Connect = connect });
        }

        private static IReadOnlyList<DeclaradException> Errors(Action action)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(action);
            return ex is ConfigErrorsException many ? many.Errors : new[] { ex };
        }

        private const string TeamModule = """
            variable "name" {
              type = string
            }
            variable "parent" {
              type = string
            }
            variable "members" {
              type    = list(object({ sam = string, first = string, last = string, admin = optional(bool, false) }))
              default = []
            }

            ou "team" {
              name = var.name
              path = var.parent
            }

            user "member" {
              for_each         = { for m in var.members : m.sam => m }
              sam_account_name = each.key
              given_name       = each.value.first
              surname          = each.value.last
              path             = ou.team.dn
              enabled          = false
            }

            group "team" {
              name    = "${var.name} Team"
              path    = ou.team.dn
              members = [for u in user.member : u.dn]
            }

            output "ou_dn" {
              value = ou.team.dn
            }
            output "admins" {
              value = [for m in var.members : user.member[m.sam].dn if m.admin]
            }
            """;

        [Fact]
        public void ExpandsModulesWithForEachAndOutputs()
        {
            var config = LoadTree(new()
            {
                ["main.dad"] = """
                    ou "corp" {
                      name = "corp"
                    }

                    module "team" {
                      for_each = {
                        eng = [{ sam = "jdoe", first = "John", last = "Doe", admin = true }]
                        ops = [{ sam = "asmith", first = "Anna", last = "Smith" }]
                      }
                      source  = "./modules/team"
                      name    = upper(each.key)
                      parent  = ou.corp.dn
                      members = each.value
                    }

                    group "admins" {
                      name = "admins"
                      members = flatten([for t in module.team : t.admins])
                    }

                    output "team_ous" {
                      value = { for k, t in module.team : k => t.ou_dn }
                    }
                    """,
                ["modules/team/main.dad"] = TeamModule,
            });

            var addresses = config.Resources.Select(r => r.Address).Order(StringComparer.Ordinal).ToList();
            Assert.Contains("module.team[\"eng\"].ou.team", addresses);
            Assert.Contains("module.team[\"eng\"].user.member[\"jdoe\"]", addresses);
            Assert.Contains("module.team[\"ops\"].group.team", addresses);

            var jdoe = config.Find("module.team[\"eng\"].user.member[\"jdoe\"]")!;
            Assert.Equal("CN=John Doe,OU=ENG,OU=corp,DC=corp,DC=local", jdoe.Dn);
            Assert.Contains("module.team[\"eng\"].ou.team", jdoe.DependsOn);
            // The module's OU depends on the root OU through the module input.
            Assert.Contains("ou.corp", config.Find("module.team[\"eng\"].ou.team")!.DependsOn);

            Assert.Equal(Value.FromStrings(new[] { jdoe.Dn }), config.Find("group.admins")!.Get("members"));
            var output = Assert.Single(config.Outputs);
            Assert.Equal(Value.From("OU=OPS,OU=corp,DC=corp,DC=local"), ((ObjectValue)output.Value).Fields["ops"]);
        }

        [Fact]
        public void ValidatesModuleInputs()
        {
            var errors = Errors(() => LoadTree(new()
            {
                ["main.dad"] = """
                    module "team" {
                      source = "./modules/team"
                      name   = "x"
                      parnet = "DC=corp,DC=local"
                    }
                    """,
                ["modules/team/main.dad"] = TeamModule,
            }));
            Assert.Contains(errors, e => e.Message.Contains("no variable 'parnet' (did you mean 'parent'?)"));
            Assert.Contains(errors, e => e.Message.Contains("requires variable 'parent'"));

            errors = Errors(() => LoadTree(new() { ["main.dad"] = "module \"x\" {\n  source = \"git::https://example.com/repo.git\"\n}\n" }));
            Assert.Contains(errors, e => e.Message.Contains("is not installed. Run 'declarad init'"));

            errors = Errors(() => LoadTree(new() { ["main.dad"] = "module \"x\" {\n  source = \"hashicorp/consul/aws\"\n}\n" }));
            Assert.Contains(errors, e => e.Message.Contains("registry"));

            errors = Errors(() => LoadTree(new()
            {
                ["main.dad"] = "module \"t\" {\n  source = \"./modules/team\"\n  name = \"x\"\n  parent = \"DC=corp,DC=local\"\n  members = [{ sam = \"a\" }]\n}\n",
                ["modules/team/main.dad"] = TeamModule,
            }));
            Assert.Contains(errors, e => e.Message.Contains("'first' is required"));
        }

        [Fact]
        public void SupportsCountAndCountIndex()
        {
            var config = ConfigTests.Load("""
                variable "n" { default = 3 }
                computer "ws" {
                  count = var.n
                  name  = format("WS%03d", count.index + 1)
                }
                """);
            Assert.Equal(new[] { "computer.ws[0]", "computer.ws[1]", "computer.ws[2]" }, config.Resources.Select(r => r.Address));
            Assert.Equal(Value.From("WS003$"), config.Find("computer.ws[2]")!.Get("sam_account_name"));
            Assert.Equal(Value.From("ws001.corp.local"), config.Find("computer.ws[0]")!.Get("dns_host_name"));
        }

        [Fact]
        public void RunsVariableValidationAndPreconditions()
        {
            const string src = """
                variable "dept" {
                  type = string
                  validation {
                    condition     = contains(["eng", "ops"], var.dept)
                    error_message = "dept must be eng or ops, got ${var.dept}."
                  }
                }
                group "g" {
                  name = var.dept
                  lifecycle {
                    precondition {
                      condition     = length(var.dept) == 3
                      error_message = "dept codes have three letters."
                    }
                  }
                }
                """;
            Assert.NotNull(ConfigTests.Load(src, new() { ["dept"] = "eng" }));
            var errors = Errors(() => ConfigTests.Load(src, new() { ["dept"] = "hr" }));
            Assert.Contains(errors, e => e.Message.Contains("dept must be eng or ops, got hr."));
        }

        [Fact]
        public void LocalsReplayDependenciesToEveryUser()
        {
            var config = ConfigTests.Load("""
                ou "base" {
                  name = "base"
                }
                locals {
                  path = ou.base.dn
                }
                user "a" {
                  sam_account_name = "a"
                  path    = local.path
                  enabled = false
                }
                user "b" {
                  sam_account_name = "b"
                  path    = local.path
                  enabled = false
                }
                """);
            Assert.Contains("ou.base", config.Find("user.a")!.DependsOn);
            Assert.Contains("ou.base", config.Find("user.b")!.DependsOn);
        }

        [Fact]
        public void CollectsMovedAndImportBlocksAndTerraformStyleResources()
        {
            var config = ConfigTests.Load("""
                resource "group" "new" { name = "Ops" }
                moved {
                  from = group.old
                  to   = group.new
                }
                import {
                  to = group.new
                  id = "CN=Ops,CN=Users,DC=corp,DC=local"
                }
                """);
            Assert.Equal(("group.old", "group.new"), (config.Moves[0].From, config.Moves[0].To));
            Assert.Equal("CN=Ops,CN=Users,DC=corp,DC=local", Assert.Single(config.Imports).Id);
            Assert.NotNull(config.Find("group.new"));
        }

        [Fact]
        public void DataSourcesAreUnknownOfflineAndReadOnline()
        {
            const string src = """
                data "group" "admins" {
                  sam_account_name = "Domain Admins"
                }
                data "gpo" "baseline" {
                  name = "Security Baseline"
                }
                ou "staff" {
                  name = "staff"
                }
                group_membership "admins" {
                  group   = data.group.admins.dn
                  members = [user.jdoe.dn]
                }
                gpo_link "baseline" {
                  gpo    = data.gpo.baseline.id
                  target = ou.staff.dn
                }
                user "jdoe" {
                  sam_account_name = "jdoe"
                  path    = ou.staff.dn
                  enabled = false
                }
                output "admins_sid" {
                  value = data.group.admins.sid
                }
                """;
            var offline = ConfigTests.Load(src);
            Assert.True(offline.Find("group_membership.admins")!.HasUnknowns);
            Assert.True(offline.Outputs[0].Value.IsUnknown);

            var dir = new InMemoryDirectory();
            var online = ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = _ => null, Connect = _ => dir });
            Assert.Same(dir, online.OpenDirectory);
            Assert.Equal("CN=Domain Admins,CN=Users,DC=corp,DC=local", online.Find("group_membership.admins")!.Dn);
            Assert.Equal(Value.From(InMemoryDirectory.BaselinePolicy), online.Find("gpo_link.baseline")!.Get("gpo"));
            Assert.Equal(Value.From("S-1-5-21-1000-2000-3000-512"), online.Outputs[0].Value);

            var errors = Errors(() => ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + "data \"gpo\" \"x\" {\n  name = \"Nope\"\n}\n") }, new LoadOptions { GetEnv = _ => null, Connect = _ => dir }));
            Assert.Contains(errors, e => e.Message.Contains("no GPO found"));
        }

        [Fact]
        public void ForEachOverUnknownValuesWarnsOffline()
        {
            var config = ConfigTests.Load("""
                data "objects" "sales" {
                  filter = "(department=Sales)"
                }
                group "sales" {
                  name = "sales"
                  members = data.objects.sales.dns
                }
                contact "c" {
                  for_each = toset(data.objects.sales.dns)
                  name     = "c-${each.key}"
                }
                """);
            Assert.Contains(config.Warnings, w => w.Contains("contact.c") && w.Contains("for_each"));
            Assert.True(config.Find("group.sales")!.HasUnknowns);
        }
    }
}
