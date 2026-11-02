using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Resources;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    /// <summary>Deletion protection on every type, "User cannot change password", delegation and the extra group, gMSA and OU attributes.</summary>
    public class ExtendedAttributeTests
    {
        private const string Jane = "CN=jroe,CN=Users,DC=corp,DC=local";
        private const string Svc = "CN=svcapp,CN=Managed Service Accounts,DC=corp,DC=local";
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

        private static string User(string extra = "") => $$"""
            user "jroe" {
              sam_account_name = "jroe"
              enabled          = false
              {{extra}}
            }
            """;

        [Fact]
        public void UserCannotChangePasswordIsTheAducDenyAce()
        {
            string on = User("user_cannot_change_password = true");
            Apply(on);
            Assert.True(SecurityDescriptor.IsChangePasswordDenied(_dir.Get(Jane)!.SecurityDescriptor));
            AssertConverged(on);

            // Removing the setting clears what declarad set.
            Apply(User());
            Assert.False(SecurityDescriptor.IsChangePasswordDenied(_dir.Get(Jane)!.SecurityDescriptor));
            AssertConverged(User());

            // Set by someone else and never mentioned in the configuration: left alone.
            _dir.SetSecurityDescriptor(Jane, SecurityDescriptor.SetChangePasswordDenied(_dir.Get(Jane)!.SecurityDescriptor!, true));
            AssertConverged(User());

            string off = User("user_cannot_change_password = false");
            Assert.Equal("user_cannot_change_password", Assert.Single(Assert.Single(Plan(off).Changes).Changes).Name);
            Apply(off);
            Assert.False(SecurityDescriptor.IsChangePasswordDenied(_dir.Get(Jane)!.SecurityDescriptor));
            Assert.False(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get(Jane)!.SecurityDescriptor));
            AssertConverged(off);
        }

        [Fact]
        public void ChangePasswordAceRoundTripsAndKeepsOtherAces()
        {
            byte[] sd = InMemoryDirectory.DefaultSd();
            byte[] denied = SecurityDescriptor.SetChangePasswordDenied(SecurityDescriptor.SetProtectedFromDeletion(sd, true), true);
            Assert.True(SecurityDescriptor.IsChangePasswordDenied(denied));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(denied));
            byte[] allowed = SecurityDescriptor.SetChangePasswordDenied(denied, false);
            Assert.False(SecurityDescriptor.IsChangePasswordDenied(allowed));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(allowed));
            Assert.Equal(SecurityDescriptor.SetProtectedFromDeletion(sd, true), allowed);
        }

        [Fact]
        public void UsersGroupsAndGmsasCanBeProtectedFromAccidentalDeletion()
        {
            const string src = """
                user "jroe" {
                  sam_account_name                   = "jroe"
                  enabled                            = false
                  protected_from_accidental_deletion = true
                }
                group "team" {
                  name                               = "team"
                  protected_from_accidental_deletion = true
                }
                service_account "svc" {
                  name                               = "svcapp"
                  protected_from_accidental_deletion = true
                }
                """;
            Apply(src);
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get(Jane)!.SecurityDescriptor));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get("CN=team,CN=Users,DC=corp,DC=local")!.SecurityDescriptor));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get(Svc)!.SecurityDescriptor));
            AssertConverged(src);

            // Removing a protected object is refused at plan time.
            var ex = Assert.ThrowsAny<DeclaradException>(() => Plan("""
                group "team" {
                  name                               = "team"
                  protected_from_accidental_deletion = true
                }
                service_account "svc" {
                  name                               = "svcapp"
                  protected_from_accidental_deletion = true
                }
                """));
            Assert.Contains("user.jroe", ex.Message);
            Assert.Contains("protected from accidental deletion", ex.Message);
        }

        [Fact]
        public void ProtectedUserCanStillBeMovedToAnotherOu()
        {
            const string ous = """
                ou "a" {
                  name                               = "a"
                  protected_from_accidental_deletion = false
                }
                ou "b" {
                  name                               = "b"
                  protected_from_accidental_deletion = false
                }

                """;
            string src = ous + """
                user "jroe" {
                  sam_account_name                   = "jroe"
                  path                               = ou.a.dn
                  enabled                            = false
                  protected_from_accidental_deletion = true
                }
                """;
            Apply(src);
            Apply(src.Replace("ou.a.dn", "ou.b.dn"));
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(_dir.Get("CN=jroe,OU=b,DC=corp,DC=local")!.SecurityDescriptor));
            AssertConverged(src.Replace("ou.a.dn", "ou.b.dn"));
        }

        [Fact]
        public void UserConstrainedDelegationAndPersonalTitle()
        {
            string src = User("""
                personal_title         = "Dr."
                allowed_to_delegate_to = ["cifs/fs01.corp.local", "cifs/fs01"]
                trusted_to_auth_for_delegation = true
                """);
            Apply(src);
            Assert.Equal("Dr.", _dir.Attr(Jane, "personalTitle"));
            Assert.Equal(2, _dir.Get(Jane)!.GetAll("msDS-AllowedToDelegateTo").Length);
            Assert.NotEqual(0, long.Parse(_dir.Attr(Jane, "userAccountControl")!) & Uac.TrustedToAuthForDelegation);
            AssertConverged(src);
        }

        [Fact]
        public void GroupExchangeAndUnixAttributes()
        {
            const string src = """
                group "team" {
                  name                    = "team"
                  category                = "distribution"
                  email                   = "team@corp.example"
                  proxy_addresses         = ["SMTP:team@corp.example", "smtp:t@corp.example"]
                  mail_nickname           = "team"
                  hide_from_address_lists = true
                  extension_attribute_3   = "cost-center-7"
                  gid_number              = 5001
                }
                """;
            Apply(src);
            const string dn = "CN=team,CN=Users,DC=corp,DC=local";
            Assert.Equal(2, _dir.Get(dn)!.GetAll("proxyAddresses").Length);
            Assert.Equal("TRUE", _dir.Attr(dn, "msExchHideFromAddressLists"));
            Assert.Equal("cost-center-7", _dir.Attr(dn, "extensionAttribute3"));
            Assert.Equal("5001", _dir.Attr(dn, "gidNumber"));
            AssertConverged(src);

            string hcl = new Exporter(_dir).Export("CN=Users,DC=corp,DC=local", new[] { ResourceRegistry.Group });
            Assert.Contains("extension_attribute_3", hcl);
            Assert.DoesNotContain(ResourceType.Protected, hcl);
        }

        [Fact]
        public void GmsaDelegationAndDescriptiveAttributes()
        {
            const string src = """
                computer "web01" {
                  name = "web01"
                }
                group "admins" {
                  name = "svc admins"
                }
                service_account "svc" {
                  name                                      = "svcapp"
                  display_name                              = "App service"
                  notes                                     = "Owned by the web team."
                  managed_by                                = group.admins.dn
                  allowed_to_delegate_to                    = ["http/backend.corp.local"]
                  principals_allowed_to_delegate_to_account = [computer.web01.dn]
                  trusted_to_auth_for_delegation            = true
                  account_is_sensitive                      = false
                }
                """;
            Apply(src);
            var svc = _dir.Get(Svc)!;
            Assert.Equal("App service", svc.Get("displayName"));
            Assert.Equal("Owned by the web team.", svc.Get("info"));
            Assert.Equal("CN=svc admins,CN=Users,DC=corp,DC=local", svc.Get("managedBy"));
            Assert.Equal("http/backend.corp.local", svc.Get("msDS-AllowedToDelegateTo"));
            var rbcd = SecurityDescriptor.AllowedSids(svc.Binary["msDS-AllowedToActOnBehalfOfOtherIdentity"][0]);
            Assert.Equal(_dir.Get("CN=web01,CN=Computers,DC=corp,DC=local")!.Sid, Assert.Single(rbcd));
            Assert.NotEqual(0, long.Parse(svc.Get("userAccountControl")!) & Uac.TrustedToAuthForDelegation);
            AssertConverged(src);

            // An empty list removes resource-based delegation.
            string none = src.Replace("[computer.web01.dn]", "[]");
            Apply(none);
            Assert.False(_dir.Get(Svc)!.Binary.ContainsKey("msDS-AllowedToActOnBehalfOfOtherIdentity"));
            AssertConverged(none);
        }

        [Fact]
        public void OuAddressAndUpnSuffixes()
        {
            const string src = """
                ou "berlin" {
                  name                               = "Berlin"
                  protected_from_accidental_deletion = false
                  office                             = "HQ"
                  phone                              = "+49 30 1234"
                  fax                                = "+49 30 1235"
                  po_box                             = "PO 7"
                  country                            = "DE"
                  country_name                       = "Germany"
                  country_code                       = 276
                  upn_suffixes                       = ["corp.de", "corp.example"]
                }
                """;
            Apply(src);
            const string dn = "OU=Berlin,DC=corp,DC=local";
            Assert.Equal("HQ", _dir.Attr(dn, "physicalDeliveryOfficeName"));
            Assert.Equal("276", _dir.Attr(dn, "countryCode"));
            Assert.Equal(2, _dir.Get(dn)!.GetAll("uPNSuffixes").Length);
            AssertConverged(src);
        }
    }
}
