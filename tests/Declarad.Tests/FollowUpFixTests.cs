using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;
using Declarad.Hcl.Values;
using Declarad.Resources.Dns;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class FollowUpFixTests
    {
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

        [Fact]
        public void MixedTtlsInARecordSetAreRewritten()
        {
            const string src = "dns_record \"a\" {\n  zone = \"corp.local\"\n  name = \"app\"\n  type = \"A\"\n  records = [\"10.0.0.1\", \"10.0.0.2\"]\n  ttl = 300\n}\n";
            Apply(src);
            const string node = "DC=app,DC=corp.local,CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=local";
            var records = _dir.Get(node)!.Binary["dnsRecord"].Select(DnsCodec.Decode).ToList();
            var tampered = records.Select((r, i) => DnsCodec.Encode(i == 0 ? r with { Ttl = 60 } : r)).Cast<object>().ToArray();
            _dir.Modify(node, new[] { new Modification("dnsRecord", ModOp.Replace, tampered) });

            var change = Plan(src).Changes.Single();
            Assert.Equal("ttl", Assert.Single(change.Changes).Name);
            Apply(src);
            Assert.All(_dir.Get(node)!.Binary["dnsRecord"].Select(DnsCodec.Decode), r => Assert.Equal(300u, r.Ttl));
        }

        [Fact]
        public void ProtectingAnOuAlsoDeniesDeleteChildOnItsParent()
        {
            Apply("ou \"parent\" {\n  protected_from_accidental_deletion = false\n}\nou \"child\" {\n  path = ou.parent.dn\n}\n");
            var parent = _dir.Get("OU=parent,DC=corp,DC=local")!;
            Assert.True(SecurityDescriptor.HasDenyEveryone(parent.SecurityDescriptor, SecurityDescriptor.DeleteChild));
            Assert.False(SecurityDescriptor.IsProtectedFromDeletion(parent.SecurityDescriptor));

            // Lifting the child's protection keeps the parent ACE (siblings may rely on it), like ADUC.
            Apply("ou \"parent\" {\n  protected_from_accidental_deletion = false\n}\nou \"child\" {\n  path = ou.parent.dn\n  protected_from_accidental_deletion = false\n}\n");
            Assert.True(SecurityDescriptor.HasDenyEveryone(_dir.Get("OU=parent,DC=corp,DC=local")!.SecurityDescriptor, SecurityDescriptor.DeleteChild));

            // Deny ACEs for different rights are edited independently.
            var sd = SecurityDescriptor.SetDenyEveryone(InMemoryDirectory.DefaultSd(), SecurityDescriptor.DeleteChild, true);
            sd = SecurityDescriptor.SetProtectedFromDeletion(sd, true);
            sd = SecurityDescriptor.SetProtectedFromDeletion(sd, false);
            Assert.True(SecurityDescriptor.HasDenyEveryone(sd, SecurityDescriptor.DeleteChild));
            Assert.False(SecurityDescriptor.IsProtectedFromDeletion(sd));
        }

        [Theory]
        [InlineData("min_password_age = \"60d\"\n  max_password_age = \"30d\"", "shorter than max_password_age")]
        [InlineData("min_password_age = \"never\"", "min_password_age cannot be")]
        [InlineData("lockout_observation_window = \"2h\"\n  lockout_duration = \"30m\"", "must not be longer than lockout_duration")]
        [InlineData("lockout_observation_window = \"never\"", "lockout_observation_window cannot be")]
        [InlineData("min_password_length = 300", "between 0 and 255")]
        [InlineData("lockout_threshold = 70000", "between 0 and 65535")]
        public void RejectsInconsistentPasswordPolicies(string settings, string message)
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load($"password_policy \"p\" {{\n  precedence = 1\n  {settings}\n}}\n"));
            Assert.Contains(message, ex is ConfigErrorsException c ? string.Join("\n", c.Errors.Select(e => e.Message)) : ex.Message);
        }

        [Fact]
        public void AcceptsConsistentPasswordPolicies()
        {
            Assert.NotNull(Load("password_policy \"p\" {\n  precedence = 1\n  max_password_age = \"never\"\n  min_password_age = \"1d\"\n  lockout_duration = \"never\"\n  lockout_observation_window = \"1h\"\n}\n"));
        }

        [Fact]
        public void UnknownValuesOnlySpreadWhereTheyMatter()
        {
            Value Eval(string expr) => new Evaluator(NoReferences.Instance, new Functions("."), new Dictionary<string, Value>
            {
                ["u"] = Value.Unknown,
                ["list"] = Value.From(new[] { Value.From("a"), Value.Unknown }),
                ["obj"] = new ObjectValue(new Dictionary<string, Value> { ["k"] = Value.From(1), ["x"] = Value.Unknown }),
            }).Evaluate(Parser.ParseExpression(expr, default));

            Assert.Equal(Value.From(2), Eval("length(list)"));
            Assert.Equal(Value.FromStrings(new[] { "k", "x" }), Eval("keys(obj)"));
            Assert.Equal(Value.From(1), Eval("lookup(obj, \"k\")"));
            Assert.Equal(Value.From(1), Eval("merge(obj, { y = 2 }).k"));
            Assert.Equal(Value.From(3), Eval("length(concat(list, [\"b\"]))"));
            Assert.Equal(Value.True, Eval("u || true"));
            Assert.Equal(Value.False, Eval("u && false"));
            Assert.True(Eval("u || false").IsUnknown);
            Assert.True(Eval("upper(u)").IsUnknown);
            Assert.True(Eval("join(\",\", list)").IsUnknown);
            Assert.True(Eval("length(u)").IsUnknown);
        }

        [Fact]
        public void ValidateKeepsKnownPartsOfDataDrivenValues()
        {
            var config = Load("""
                data "objects" "o" {
                  filter = "(objectClass=user)"
                }
                group "g" {
                  description = length(data.objects.o.dns) > 0 || true ? "has members" : "empty"
                }
                """.Replace("data \"objects\" \"o\" {", "data \"objects\" \"o\" {"));
            Assert.NotNull(config);
            var offline = ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + "data \"domain\" \"d\" {}\ngroup \"g\" {\n  description = data.domain.d.name == \"x\" || true ? \"known\" : \"no\"\n}\n") }, new LoadOptions { GetEnv = _ => null });
            Assert.Equal(Value.From("known"), offline.Find("group.g")!.Get("description"));
        }
    }
}
