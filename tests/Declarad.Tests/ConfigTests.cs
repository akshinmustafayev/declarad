using Declarad.Config;
using Declarad.Hcl.Values;

namespace Declarad.Tests
{
    public class ConfigTests
    {
        internal const string Connection = """
            connection {
              server   = "dc01.corp.local"
              domain   = "corp.local"
              username = "admin@corp.local"
              password = "secret"
            }

            """;

        internal static Configuration Load(string source, Dictionary<string, string>? vars = null, Dictionary<string, string>? env = null) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", Connection + source) }, vars, n => env?.GetValueOrDefault(n));

        private static IReadOnlyList<DeclaradException> Errors(string source)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load(source));
            return ex is ConfigErrorsException many ? many.Errors : new[] { ex };
        }

        [Fact]
        public void AppliesLabelsAndDefaults()
        {
            var config = Load("""
                ou "Sales" {}
                user "jdoe" {
                  given_name = "John"
                  surname    = "Doe"
                  path       = ou.Sales.dn
                }
                group "admins" {}
                """);

            var ou = config.Find("ou.Sales")!;
            Assert.Equal("OU=Sales,DC=corp,DC=local", ou.Dn);
            Assert.Equal(Value.True, ou.Get("protected_from_accidental_deletion"));

            var user = config.Find("user.jdoe")!;
            Assert.Equal("CN=John Doe,OU=Sales,DC=corp,DC=local", user.Dn);
            Assert.Equal(Value.From("jdoe"), user.Get("sam_account_name"));
            Assert.Equal(Value.From("John Doe"), user.Get("display_name"));
            Assert.Equal(Value.From("jdoe@corp.local"), user.Get("user_principal_name"));
            Assert.Equal(Value.True, user.Get("enabled"));
            Assert.Contains("ou.Sales", user.DependsOn);

            var group = config.Find("group.admins")!;
            Assert.Equal("CN=admins,CN=Users,DC=corp,DC=local", group.Dn);
            Assert.Equal(Value.From("global"), group.Get("scope"));
            Assert.True(group.Get("members").IsNull);
        }

        [Fact]
        public void ResolvesVariablesLocalsAndOverrides()
        {
            const string src = """
                variable "dept" { default = "Engineering" }
                variable "enabled" { default = true }
                locals {
                  base = "OU=${var.dept},DC=corp,DC=local"
                }
                ou "dept" {
                  name = var.dept
                }
                user "x" {
                  path    = local.base
                  enabled = var.enabled
                  initial_password = "P@ssw0rd!"
                }
                """;
            var config = Load(src);
            Assert.Equal("CN=x,OU=Engineering,DC=corp,DC=local", config.Find("user.x")!.Dn);

            config = Load(src, new() { ["dept"] = "Ops", ["enabled"] = "false" });
            Assert.Equal("CN=x,OU=Ops,DC=corp,DC=local", config.Find("user.x")!.Dn);
            Assert.Equal(Value.False, config.Find("user.x")!.Get("enabled"));

            config = Load(src, env: new() { ["DECLARAD_VAR_dept"] = "Env" });
            Assert.Equal("OU=Env,DC=corp,DC=local", config.Find("ou.dept")!.Dn);
        }

        [Fact]
        public void ExpandsForEachFromCsvRows()
        {
            var config = Load("""
                locals {
                  rows  = csvdecode("sam,first,last,dept\njdoe,John,Doe,eng\nasmith,Anna,Smith,ops\n")
                  users = { for r in local.rows : r.sam => r }
                }
                ou "staff" {}
                user "staff" {
                  for_each         = local.users
                  sam_account_name = each.key
                  given_name       = each.value.first
                  surname          = each.value.last
                  department       = upper(each.value.dept)
                  path             = ou.staff.dn
                  enabled          = false
                }
                group "everyone" {
                  path    = ou.staff.dn
                  members = [for u in user.staff : u.dn]
                }
                """);

            Assert.Equal(new[] { "group.everyone", "ou.staff", "user.staff[\"asmith\"]", "user.staff[\"jdoe\"]" },
                config.Resources.Select(r => r.Address).Order(StringComparer.Ordinal));
            var jdoe = config.Find("user.staff[\"jdoe\"]")!;
            Assert.Equal("CN=John Doe,OU=staff,DC=corp,DC=local", jdoe.Dn);
            Assert.Equal(Value.From("ENG"), jdoe.Get("department"));
            Assert.Equal(2, ((ListValue)config.Find("group.everyone")!.Get("members")).Items.Length);
            // Membership is deferred, so it does not create an ordering dependency.
            Assert.DoesNotContain("user.staff[\"jdoe\"]", config.Find("group.everyone")!.DependsOn);
        }

        [Fact]
        public void MutualMembershipReferencesDoNotCycle()
        {
            var config = Load("""
                group "a" { members = [group.b.dn] }
                group "b" { members = [group.a.dn] }
                """);
            Assert.Equal(2, config.Resources.Count);
        }

        [Fact]
        public void ReportsAllErrorsWithSuggestions()
        {
            var errors = Errors("""
                user "jdoe" {
                  surnme = "Doe"
                }
                group "g" {
                  scope = "planetary"
                }
                ou "o" {
                  country = "Azerbaijan"
                }
                widget "w" {}
                """);

            Assert.Contains(errors, e => e.Message.Contains("did you mean 'surname'"));
            Assert.Contains(errors, e => e.Message.Contains("Unknown block type 'widget'"));
            Assert.Equal("main.dad", errors[0].Pos.File);

            errors = Errors("""
                group "g" { scope = "planetary" }
                ou "o" { country = "Azerbaijan" }
                user "toolongnameforasamaccount" {}
                """);
            Assert.Contains(errors, e => e.Message.Contains("scope must be one of"));
            Assert.Contains(errors, e => e.Message.Contains("two-letter"));
            Assert.Contains(errors, e => e.Message.Contains("1-20 characters"));
        }

        [Fact]
        public void DetectsDuplicatesAndBadReferences()
        {
            var errors = Errors("""
                user "a" { name = "Same" }
                user "b" { name = "Same" }
                """);
            Assert.Contains(errors, e => e.Message.Contains("same distinguished name"));

            errors = Errors("""
                user "dup" { name = "One" }
                group "dup" {}
                """);
            Assert.Contains(errors, e => e.Message.Contains("same sam_account_name"));

            errors = Errors("""user "a" { path = ou.missing.dn }""");
            Assert.Contains(errors, e => e.Message.Contains("undeclared resource ou.missing"));

            errors = Errors("""user "a" { path = var.nope }""");
            Assert.Contains(errors, e => e.Message.Contains("Undeclared variable 'nope'"));

            errors = Errors("""
                ou "a" { path = ou.b.dn }
                ou "b" { path = ou.a.dn }
                """);
            Assert.Contains(errors, e => e.Message.Contains("Cycle detected"));
        }

        [Fact]
        public void ParsesLifecycleAndDependsOn()
        {
            var config = Load("""
                ou "a" {}
                group "g" {
                  depends_on = [ou.a]
                  lifecycle {
                    prevent_destroy = true
                    ignore_changes  = [description, "members"]
                  }
                }
                """);
            var g = config.Find("group.g")!;
            Assert.True(g.PreventDestroy);
            Assert.Equal(new[] { "description", "members" }, g.IgnoreChanges.Order());
            Assert.Contains("ou.a", g.DependsOn);
        }

        [Fact]
        public void ResolvesConnectionWithEnvironmentFallbacks()
        {
            var config = ConfigLoader.LoadSources(".", new[] { ("c.dad", """
                connection {
                  server   = "dc01"
                  base_dn  = "DC=example,DC=org"
                  protocol = "starttls"
                }
                """) });
            Assert.Equal("example.org", config.Domain);
            var settings = config.ResolveConnection(n => n switch { "DECLARAD_USERNAME" => "u", "DECLARAD_PASSWORD" => "p", _ => null }, null);
            Assert.Equal(("u", "p", 389), (settings.Username, settings.Password, settings.EffectivePort));
            Assert.Equal(Declarad.Directory.LdapProtocol.StartTls, settings.Protocol);

            var ex = Assert.Throws<DeclaradException>(() => config.ResolveConnection(_ => null, null));
            Assert.Contains("username", ex.Message);
        }
    }
}
