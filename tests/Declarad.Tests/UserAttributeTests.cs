using Declarad.Config;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Resources;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>The user attributes beyond names: telephones, address, account options, Exchange and Unix attributes.</summary>
    public class UserAttributeTests
    {
        private const string Dn = "CN=Jane Roe,CN=Users,DC=corp,DC=local";
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
            Assert.Empty(Plan(src).Changes.Where(c => c.Action != ChangeAction.NoOp).Select(c => $"{c.Address}: {string.Join(", ", c.Changes.Select(x => $"{x.Name} {x.Old}->{x.New}"))}"));

        private long Uac() => long.Parse(_dir.Attr(Dn, "userAccountControl")!);

        private const string Full = """
            user "jroe" {
              sam_account_name = "jroe"
              given_name              = "Jane"
              surname                 = "Roe"
              middle_name             = "Q"
              enabled                 = false
              division                = "R&D"
              home_phone              = "+1 555 0100"
              other_phones            = ["+1 555 0101", "+1 555 0102"]
              pager                   = "555 0103"
              fax                     = "+1 555 0104"
              ip_phone                = "4711"
              notes                   = "Prefers e-mail."
              po_box                  = "PO 42"
              country                 = "DE"
              country_name            = "Germany"
              country_code            = 276
              other_web_pages         = ["https://intranet/jroe"]
              preferred_language      = "de-DE"
              logon_workstations      = ["WS01", "WS02"]
              smartcard_required      = true
              account_is_sensitive    = true
              extension_attribute_1   = "cost-center-7"
              mail_nickname           = "jroe"
              hide_from_address_lists = true
              uid_number              = 10001
              login_shell             = "/bin/bash"
            }
            """;

        [Fact]
        public void CreatesUserWithExtendedAttributesAndConverges()
        {
            Apply(Full);

            Assert.Equal("Q", _dir.Attr(Dn, "middleName"));
            Assert.Equal("R&D", _dir.Attr(Dn, "division"));
            Assert.Equal(new[] { "+1 555 0101", "+1 555 0102" }, _dir.Get(Dn)!.GetAll("otherTelephone").Order());
            Assert.Equal("Prefers e-mail.", _dir.Attr(Dn, "info"));
            Assert.Equal("276", _dir.Attr(Dn, "countryCode"));
            Assert.Equal("Germany", _dir.Attr(Dn, "co"));
            Assert.Equal("WS01,WS02", _dir.Attr(Dn, "userWorkstations"));
            Assert.Equal("cost-center-7", _dir.Attr(Dn, "extensionAttribute1"));
            Assert.Equal("TRUE", _dir.Attr(Dn, "msExchHideFromAddressLists"));
            Assert.Equal("10001", _dir.Attr(Dn, "uidNumber"));
            Assert.NotEqual(0, Uac() & 0x40000);    // smart card required
            Assert.NotEqual(0, Uac() & 0x100000);   // sensitive, cannot be delegated
            Assert.NotEqual(0, Uac() & 0x2);        // still disabled
            AssertConverged(Full);
        }

        [Fact]
        public void UpdatesAndClearsExtendedAttributes()
        {
            Apply(Full);
            const string changed = """
                user "jroe" {
                  sam_account_name = "jroe"
                  given_name           = "Jane"
                  surname              = "Roe"
                  enabled              = false
                  other_phones         = ["+1 555 0199"]
                  logon_workstations   = []
                  smartcard_required   = false
                  account_is_sensitive = true
                }
                """;

            var change = Assert.Single(Plan(changed).Changes);
            Assert.Contains(change.Changes, c => c.Name == "middle_name");
            Assert.Contains(change.Changes, c => c.Name == "logon_workstations");

            Apply(changed);
            Assert.Null(_dir.Attr(Dn, "middleName"));
            Assert.Null(_dir.Attr(Dn, "extensionAttribute1"));
            Assert.Null(_dir.Attr(Dn, "userWorkstations"));
            Assert.Equal(new[] { "+1 555 0199" }, _dir.Get(Dn)!.GetAll("otherTelephone"));
            Assert.Equal(0, Uac() & 0x40000);
            Assert.NotEqual(0, Uac() & 0x100000);
            AssertConverged(changed);
        }

        [Fact]
        public void AccountOptionsAreUnmanagedUnlessSet()
        {
            Apply("""
                user "jroe" {
                  sam_account_name = "jroe"
                  given_name = "Jane"
                  surname    = "Roe"
                  enabled    = false
                }
                """);
            // Someone ticks "Smart card is required" in ADUC; the configuration doesn't mention it.
            _dir.Modify(Dn, new[] { Directory.Modification.Replace("userAccountControl", (Uac() | 0x40000).ToString()) });

            AssertConverged("""
                user "jroe" {
                  sam_account_name = "jroe"
                  given_name = "Jane"
                  surname    = "Roe"
                  enabled    = false
                }
                """);
        }

        [Fact]
        public void ExportWritesOnlyAccountOptionsThatAreOn()
        {
            Apply(Full);
            string hcl = new Exporter(_dir).Export("CN=Users,DC=corp,DC=local", new[] { ResourceRegistry.User });

            Assert.Contains("smartcard_required", hcl);
            Assert.Contains("logon_workstations", hcl);
            Assert.DoesNotContain("dont_require_preauth", hcl);
            Assert.DoesNotContain("trusted_for_delegation", hcl);
        }

        [Fact]
        public void RejectsWorkstationNamesWithCommas()
        {
            var ex = Assert.ThrowsAny<DeclaradException>(() => Load("""
                user "jroe" {
                  sam_account_name = "jroe"
                  enabled            = false
                  logon_workstations = ["WS01,WS02"]
                }
                """));
            Assert.Contains("must not be empty or contain commas", ex.Message);
        }
    }
}
