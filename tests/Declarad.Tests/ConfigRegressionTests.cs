using Declarad.Config;
using Declarad.Config.Modules;

namespace Declarad.Tests
{
    /// <summary>Regression tests for bugs found in review of configuration loading.</summary>
    public class ConfigRegressionTests
    {
        private static IReadOnlyList<DeclaradException> Errors(Action action)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(action);
            return ex is ConfigErrorsException many ? many.Errors : new[] { ex };
        }

        [Fact]
        public void LocalsCannotSeeTheEvaluatingInstance()
        {
            var errors = Errors(() => ConfigTests.Load("""
                locals { desc = "dept-${each.key}" }
                ou "d" {
                  for_each    = toset(["a", "b"])
                  name        = each.key
                  description = local.desc
                }
                """));
            Assert.Contains(errors, e => e.Message.Contains("'each' can only be used"));

            errors = Errors(() => ConfigTests.Load("""
                ou "a" {
                  for_each = toset(["k1"])
                  name     = each.key
                  path     = ou.b["k1"].dn
                }
                ou "b" {
                  for_each = toset([each.key])
                  name     = each.key
                }
                """));
            Assert.Contains(errors, e => e.Message.Contains("'each' can only be used"));
        }

        [Fact]
        public void OutputDependsOnAndModuleExpansionDependenciesReachCallers()
        {
            var config = ModuleTests.LoadTree(new()
            {
                ["main.dad"] = """
                    ou "depts" {
                      for_each = toset(["a", "b"])
                      name     = each.key
                    }
                    module "m" {
                      source   = "./m"
                      for_each = ou.depts
                      parent   = each.value.dn
                    }
                    module "o" {
                      source = "./o"
                    }
                    group "g" {
                      name        = "G"
                      description = module.o.x
                    }
                    """,
                ["m/main.dad"] = "variable \"parent\" {}\ngroup \"g\" {\n  name = \"Team ${basename(var.parent)}\"\n  path = var.parent\n}\n",
                ["o/main.dad"] = "ou \"inner\" {\n  name = \"Inner\"\n}\noutput \"x\" {\n  value      = \"hello\"\n  depends_on = [ou.inner]\n}\n",
            });
            Assert.Contains("module.o.ou.inner", config.Find("group.g")!.DependsOn);
            Assert.Contains("ou.depts[\"a\"]", config.Find("module.m[\"a\"].group.g")!.DependsOn);
        }

        [Fact]
        public void IndexedReferencesToUnknownExpansionsAreUnknownOffline()
        {
            var config = ModuleTests.LoadTree(new()
            {
                ["main.dad"] = """
                    data "objects" "o" {
                      filter = "(objectClass=organizationalUnit)"
                    }
                    data "domain" "d" {}
                    ou "x" {
                      for_each = toset(data.objects.o.dns)
                      name     = each.key
                    }
                    group "g" {
                      name = "G"
                      path = ou.x["a"].dn
                      lifecycle {
                        prevent_destroy = data.domain.d.name == "corp.local"
                      }
                    }
                    module "m" {
                      source = "./m"
                      count  = length(data.objects.o.dns)
                    }
                    output "first" {
                      value = module.m[0].o
                    }
                    """,
                ["m/main.dad"] = "output \"o\" {\n  value = 1\n}\n",
            });
            Assert.True(config.Find("group.g")!.HasUnknowns);
            Assert.True(config.Outputs.Single().Value.IsUnknown);
        }

        [Fact]
        public void SensitiveVariablesPropagateToOutputsAndAttributes()
        {
            const string src = """
                variable "secret" {
                  default   = "hunter2"
                  sensitive = true
                }
                ou "x" {
                  name = "x"
                  description = "pw: ${var.secret}"
                }
                output "s" {
                  value = var.secret
                }
                """;
            Assert.Contains(Errors(() => ConfigTests.Load(src)), e => e.Message.Contains("add sensitive = true"));
            var config = ConfigTests.Load(src.Replace("value = var.secret", "value     = var.secret\n  sensitive = true"));
            Assert.Contains("description", config.Find("ou.x")!.SensitiveAttributes);
            Assert.True(config.Outputs.Single().Sensitive);
        }

        [Fact]
        public void VariableValuesFromTheCommandLineBehaveLikeTerraform()
        {
            const string src = """
                variable "n" { default = 1 }
                variable "flag" { default = false }
                variable "names" {
                  type    = list(string)
                  default = []
                }
                """;
            Assert.Contains(Errors(() => ConfigTests.Load(src, new() { ["nmaes"] = "x" })), e => e.Message.Contains("no variable \"nmaes\"") && e.Message.Contains("names"));
            Assert.Contains(Errors(() => ConfigTests.Load(src, new() { ["n"] = "abc" })), e => e.Message.Contains("expected number"));
            Assert.Contains(Errors(() => ConfigTests.Load(src, new() { ["flag"] = "yes" })), e => e.Message.Contains("expected bool"));
            Assert.NotNull(ConfigTests.Load(src, new() { ["n"] = "5", ["flag"] = "true", ["names"] = "[\"a\"]" }));
        }

        [Fact]
        public void ModuleErrorsAreReportedOnceAndEvenForZeroInstances()
        {
            const string module = "variable \"v\" {}\nuser \"u\" {\n  sam_account_name = \"u\"\n  bogus = 1\n}\n";
            var errors = Errors(() => ModuleTests.LoadTree(new()
            {
                ["main.dad"] = "module \"m\" {\n  source   = \"./m\"\n  for_each = toset([\"a\", \"b\", \"c\"])\n  typo     = 1\n}\n",
                ["m/main.dad"] = module,
            }));
            Assert.Equal(3, errors.Count);

            errors = Errors(() => ModuleTests.LoadTree(new()
            {
                ["main.dad"] = "module \"m\" {\n  source = \"./m\"\n  count  = 0\n}\n",
                ["m/main.dad"] = module,
            }));
            Assert.Contains(errors, e => e.Message.Contains("bogus"));
            Assert.Contains(errors, e => e.Message.Contains("requires variable 'v'"));
        }

        [Fact]
        public void NullForNonNullableModuleVariableSaysSo()
        {
            var errors = Errors(() => ModuleTests.LoadTree(new()
            {
                ["main.dad"] = "module \"m\" {\n  source = \"./m\"\n  v      = null\n}\n",
                ["m/main.dad"] = "variable \"v\" {\n  nullable = false\n}\n",
            }));
            Assert.Contains(errors, e => e.Message.Contains("must not be null"));
        }

        [Theory]
        [InlineData("port = 99999999999", "port must be a whole number")]
        [InlineData("timeout = 0", "timeout must be a whole number")]
        public void ValidatesConnectionNumbers(string setting, string message)
        {
            var config = ConfigLoader.LoadSources(".", new[] { ("c.dad", $"connection {{\n  server = \"dc\"\n  domain = \"corp.local\"\n  username = \"u\"\n  password = \"p\"\n  {setting}\n}}\n") });
            Assert.Contains(message, Assert.Throws<DeclaradException>(() => config.ResolveConnection(_ => null, null)).Message);
        }

        [Fact]
        public void RejectsBadDomainsAndHugeCounts()
        {
            Assert.Contains(Errors(() => ConfigLoader.LoadSources(".", new[] { ("c.dad", "connection {\n  server = \"dc\"\n  domain = \"\"\n}\n") })), e => e.Message.Contains("must not be empty"));
            var trimmed = ConfigLoader.LoadSources(".", new[] { ("c.dad", "connection {\n  server = \"dc\"\n  domain = \"corp.local.\"\n}\n") });
            Assert.Equal(("corp.local", "DC=corp,DC=local"), (trimmed.Domain, trimmed.BaseDn));
            Assert.Contains(Errors(() => ConfigTests.Load("computer \"c\" {\n  count = 3000000000\n  name = \"C${count.index}\"\n}\n")), e => e.Message.Contains("count must be a whole number between 0 and"));
        }

        [Theory]
        [InlineData("git::https://host/repo.git?ref=v1&ref=v2", "repeats the parameter")]
        [InlineData("git::https://host/repo.git//../escape", "may not contain '..'")]
        [InlineData("git::https://host/repo.git?ref=", "empty ref")]
        public void RejectsMalformedModuleSources(string source, string message) =>
            Assert.Contains(message, Assert.Throws<DeclaradException>(() => ModuleSource.Parse(source)).Message);

        [Fact]
        public void ArchiveSourcesKeepSignedUrlParameters()
        {
            var parsed = ModuleSource.Parse("https://host/m.zip//sub?sv=2020&sig=abc");
            Assert.Equal(("https://host/m.zip?sv=2020&sig=abc", "sub", "zip"), (parsed.Url, parsed.Subdir, parsed.ArchiveType));
        }

        [Fact]
        public void RejectsStrayNestedBlocks()
        {
            var errors = Errors(() => ConfigTests.Load("""
                data "domain" "d" {
                  lifecycle {}
                }
                locals {
                  x {}
                }
                group "g" {
                  name = "g"
                  lifecycle {}
                  lifecycle {}
                }
                """));
            Assert.Contains(errors, e => e.Message.Contains("inside data"));
            Assert.Contains(errors, e => e.Message.Contains("inside locals"));
            Assert.Contains(errors, e => e.Message.Contains("more than one lifecycle"));
        }
    }
}
