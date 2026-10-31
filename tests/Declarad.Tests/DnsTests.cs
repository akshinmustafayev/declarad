using Declarad.Config;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Resources;
using Declarad.Resources.Dns;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class DnsTests
    {
        private const string ZonesContainer = "CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=local";
        private readonly InMemoryDirectory _dir = new();
        private StateFile _state = new();

        private Configuration Load(string src) =>
            ConfigLoader.LoadSources(".", new[] { ("main.dad", ConfigTests.Connection + src) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null, Connect = _ => _dir });

        private Plan Plan(string src, PlanOptions? options = null) => new Planner(_dir, Load(src), _state, options ?? new PlanOptions()).Build();

        private void Apply(string src, PlanOptions? options = null)
        {
            var result = new Applier(_dir, _state, s => _state = StateFile.FromJson(s.ToJson()), NullLogger.Instance).Apply(Plan(src, options));
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }

        private void AssertConverged(string src) =>
            Assert.Empty(Plan(src).Changes.Where(c => c.Action != ChangeAction.NoOp).Select(c => $"{c.Address}: {c.Action} {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old}->{x.New}"))}"));

        private List<DnsCodec.Record> Records(string nodeDn) =>
            _dir.Get(nodeDn)!.Binary["dnsRecord"].Select(DnsCodec.Decode).ToList();

        [Theory]
        [InlineData("A", "10.0.0.1")]
        [InlineData("AAAA", "2001:db8::1")]
        [InlineData("CNAME", "web.corp.example.")]
        [InlineData("MX", "10 mail.corp.example.")]
        [InlineData("SRV", "0 100 389 dc01.corp.example.")]
        [InlineData("TXT", "v=spf1 -all")]
        [InlineData("PTR", "host.corp.example.")]
        public void RoundTripsRecordData(string type, string text)
        {
            var record = new DnsCodec.Record(DnsCodec.Types[type], 300, 5, DnsCodec.EncodeData(type, text));
            var decoded = DnsCodec.Decode(DnsCodec.Encode(record));
            Assert.Equal((record.Type, 300u, 5u), (decoded.Type, decoded.Ttl, decoded.Serial));
            Assert.Equal(text, DnsCodec.DecodeData(decoded.Type, decoded.Data));
        }

        [Fact]
        public void EncodesWireDetailsAsMsDnsp()
        {
            var bytes = DnsCodec.Encode(new DnsCodec.Record(1, 3600, 1, DnsCodec.EncodeData("A", "10.1.2.3")));
            Assert.Equal(new byte[] { 4, 0, 1, 0, 5, 0xF0, 0, 0, 1, 0, 0, 0, 0, 0, 0x0E, 0x10 }, bytes[..16]);
            Assert.Equal(new byte[] { 10, 1, 2, 3 }, bytes[24..]);

            var name = DnsCodec.CountName("dc01.corp.local.");
            Assert.Equal(new byte[] { 17, 3, 4, (byte)'d', (byte)'c', (byte)'0', (byte)'1', 4, (byte)'c', (byte)'o', (byte)'r', (byte)'p', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0 }, name);

            string longText = new('x', 600);
            var txt = DnsCodec.EncodeData("TXT", longText);
            Assert.Equal(new byte[] { 255 }, txt[..1]);
            Assert.Equal(longText, DnsCodec.DecodeData(16, txt));

            var soa = new DnsCodec.Soa(9, 900, 600, 86400, 3600, "ns1.z.example.", "hostmaster.z.example.");
            Assert.Equal(soa, DnsCodec.DecodeSoa(DnsCodec.EncodeSoa(soa)));
            var property = DnsCodec.DecodeProperty(DnsCodec.EncodeProperty(DnsCodec.PropertyAllowUpdate, new byte[] { 2 }))!.Value;
            Assert.Equal(DnsCodec.PropertyAllowUpdate, property.Id);
            Assert.Equal(new byte[] { 2 }, property.Data);
        }

        [Theory]
        [InlineData("CNAME", "web", "web.z.example.")]
        [InlineData("CNAME", "External.Example.Net.", "external.example.net.")]
        [InlineData("MX", "10   mail", "10 mail.z.example.")]
        [InlineData("SRV", "0 100 389 @", "0 100 389 z.example.")]
        [InlineData("AAAA", "2001:0db8:0000::0001", "2001:db8::1")]
        public void CanonicalizesConfiguredRecords(string type, string input, string expected) =>
            Assert.Equal(expected, DnsCodec.Canonicalize(type, input, "z.example"));

        [Fact]
        public void ManagesZonesWithSoaAndNameServers()
        {
            const string src = """
                dns_zone "example" {
                  name         = "Corp.Example."
                  admin_email  = "dns-admins@corp.example"
                  name_servers = ["dc01.corp.local.", "dc02.corp.local."]
                }
                """;
            var plan = Plan(src);
            Assert.Equal($"DC=corp.example,{ZonesContainer}", plan.Changes.Single().TargetDn);
            Apply(src);

            string zone = $"DC=corp.example,{ZonesContainer}";
            Assert.True(_dir.Exists(zone));
            var apex = Records($"DC=@,{zone}");
            var soa = DnsCodec.DecodeSoa(apex.Single(r => r.TypeName == "SOA").Data);
            Assert.Equal(("dc01.corp.local.", "dns-admins.corp.example.", 900u), (soa.PrimaryServer, soa.AdminEmail, soa.Refresh));
            Assert.Equal(2, apex.Count(r => r.TypeName == "NS"));
            AssertConverged(src);

            Apply(src.Replace("admin_email", "refresh      = 1200\n  admin_email"));
            soa = DnsCodec.DecodeSoa(Records($"DC=@,{zone}").Single(r => r.TypeName == "SOA").Data);
            Assert.Equal((1200u, 2u), (soa.Refresh, soa.Serial));

            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan(src.Replace("dns_zone \"example\" {", "dns_zone \"example\" {\n  replication_scope = \"forest\"")));
            Assert.Contains("cannot be changed after creation", ex is ConfigErrorsException c ? c.Errors[0].Message : ex.Message);
        }

        [Fact]
        public void ManagesRecordSetsSharingANode()
        {
            const string src = """
                dns_zone "example" {
                  name = "corp.example"
                }
                dns_record "www_a" {
                  zone    = dns_zone.example.name
                  name    = "www"
                  type    = "A"
                  records = ["10.0.0.2", "10.0.0.1"]
                  ttl     = 300
                }
                dns_record "www_txt" {
                  zone    = dns_zone.example.name
                  name    = "www"
                  type    = "TXT"
                  records = ["owner=web-team"]
                }
                dns_record "mx" {
                  zone    = dns_zone.example.name
                  name    = "@"
                  type    = "MX"
                  records = ["10 mail", "20 backup.other.example."]
                }
                """;
            Apply(src);
            string zone = $"DC=corp.example,{ZonesContainer}";
            string www = $"DC=www,{zone}";
            var records = Records(www);
            Assert.Equal(3, records.Count);
            Assert.All(records.Where(r => r.TypeName == "A"), r => Assert.Equal(300u, r.Ttl));
            Assert.Contains(Records($"DC=@,{zone}"), r => r.TypeName == "MX" && DnsCodec.DecodeData(r.Type, r.Data) == "10 mail.corp.example.");
            Assert.True(DnsCodec.DecodeSoa(Records($"DC=@,{zone}").Single(r => r.TypeName == "SOA").Data).Serial > 1);
            AssertConverged(src);

            // Drift: someone adds an A record by hand; the record set is authoritative.
            var tampered = _dir.Get(www)!.Binary["dnsRecord"].Append(DnsCodec.Encode(new(1, 300, 1, DnsCodec.EncodeData("A", "10.9.9.9")))).Cast<object>().ToArray();
            _dir.Modify(www, new[] { new Declarad.Directory.Modification("dnsRecord", Declarad.Directory.ModOp.Replace, tampered) });
            var drift = Plan(src).Changes.Single(c => c.Address == "dns_record.www_a").Get("records")!;
            Assert.Equal(new[] { "10.9.9.9" }, drift.Removed);
            Apply(src);
            AssertConverged(src);

            // Removing one record set keeps the node for the other type; removing the last deletes it.
            string withoutA = src[..src.IndexOf("dns_record \"www_a\"", StringComparison.Ordinal)] + src[src.IndexOf("dns_record \"www_txt\"", StringComparison.Ordinal)..];
            Apply(withoutA);
            Assert.Equal(new[] { "TXT" }, Records(www).Select(r => r.TypeName));
            string withoutWww = withoutA[..withoutA.IndexOf("dns_record \"www_txt\"", StringComparison.Ordinal)] + withoutA[withoutA.IndexOf("dns_record \"mx\"", StringComparison.Ordinal)..];
            Apply(withoutWww);
            Assert.False(_dir.Exists(www));

            // Renaming a record moves it to a new node.
            Apply(withoutWww.Replace("name    = \"@\"", "name    = \"mail\""));
            Assert.DoesNotContain(Records($"DC=@,{zone}"), r => r.TypeName == "MX");
            Assert.Equal(2, Records($"DC=mail,{zone}").Count);

            // Destroying the zone removes every node in it.
            Apply("", new PlanOptions { Destroy = true });
            Assert.False(_dir.Exists(zone));
        }

        [Fact]
        public void AddsRecordsToUnmanagedZonesAndValidates()
        {
            const string src = """
                dns_record "ldap" {
                  zone    = "corp.local"
                  name    = "_ldap._tcp.dc._msdcs"
                  type    = "SRV"
                  records = ["0 100 389 dc01"]
                }
                """;
            Apply(src);
            var node = Records($"DC=_ldap._tcp.dc._msdcs,DC=corp.local,{ZonesContainer}");
            Assert.Equal("0 100 389 dc01.corp.local.", DnsCodec.DecodeData(node[0].Type, node[0].Data));
            Assert.Equal(7u, node[0].Serial);
            AssertConverged(src);

            var errors = Assert.ThrowsAny<DeclaradException>(() => Load("dns_record \"c\" {\n  zone = \"corp.local\"\n  name = \"x\"\n  type = \"CNAME\"\n  records = [\"a\", \"b\"]\n}"));
            Assert.Contains("only one record", errors is ConfigErrorsException ce ? ce.Errors[0].Message : errors.Message);
            errors = Assert.ThrowsAny<DeclaradException>(() => Load("dns_record \"c\" {\n  zone = \"corp.local\"\n  name = \"x\"\n  type = \"A\"\n  records = [\"not-an-ip\"]\n}"));
            Assert.Contains("not an IPv4 address", errors is ConfigErrorsException ce2 ? ce2.Errors[0].Message : errors.Message);

            var missing = Plan("dns_record \"m\" {\n  zone = \"nowhere.example\"\n  name = \"x\"\n  type = \"A\"\n  records = [\"10.0.0.1\"]\n}");
            var result = new Applier(_dir, _state, _ => { }, NullLogger.Instance).Apply(missing);
            Assert.Contains("does not exist", Assert.Single(result.Errors));
        }

        [Fact]
        public void ExportsZonesAndStaticRecords()
        {
            Apply("""
                dns_zone "example" {
                  name = "corp.example"
                }
                dns_record "web" {
                  zone    = dns_zone.example.name
                  name    = "web"
                  type    = "CNAME"
                  records = ["www"]
                }
                """);
            // A dynamically registered client record is skipped by default.
            _dir.Add($"DC=laptop1,DC=corp.example,{ZonesContainer}", new Dictionary<string, object[]>
            {
                ["objectClass"] = new object[] { "dnsNode" },
                ["dnsRecord"] = new object[] { DnsCodec.Encode(new(1, 1200, 2, DnsCodec.EncodeData("A", "10.5.5.5"), Timestamp: 3_700_000)) },
            });
            string hcl = new Exporter(_dir).Export(null, new[] { ResourceRegistry.DnsZone, ResourceRegistry.DnsRecord });
            Assert.Contains("dns_zone \"corp_example\"", hcl);
            Assert.Contains("records = [\"www\"]", hcl);
            Assert.DoesNotContain("laptop1", hcl);

            _state = new StateFile();
            var config = ConfigLoader.LoadSources(".", new[] { ("c.dad", ConfigTests.Connection), ("e.dad", hcl) }, new LoadOptions { GetEnv = n => n == "DECLARAD_SERVER" ? "dc01.corp.local" : null });
            var plan = new Planner(_dir, config, _state, new PlanOptions { Adopt = true }).Build();
            Assert.All(plan.Changes, c => Assert.Equal(ChangeAction.NoOp, c.Action));
            Assert.Equal(3, plan.Changes.Count(c => c.Adopt)); // both zones (corp.local pre-existed) and the record
        }
    }
}
