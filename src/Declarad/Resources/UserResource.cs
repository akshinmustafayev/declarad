using System.Text;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;
using Microsoft.Extensions.Logging;

namespace Declarad.Resources
{
    /// <summary>userAccountControl flags shared by users, computers and service accounts.</summary>
    internal static class Uac
    {
        public const long AccountDisable = 0x2;
        public const long PasswordNotRequired = 0x20;
        public const long NormalAccount = 0x200;
        public const long WorkstationTrust = 0x1000;
        public const long DontExpirePassword = 0x10000;

        public static long Set(long uac, long flag, bool? on) => on switch
        {
            true => uac | flag,
            false => uac & ~flag,
            null => uac,
        };

        public static long WithEnabled(long uac, Value enabled) => Set(uac, AccountDisable, V.Bool(enabled) is { } e ? !e : null);
    }

    public sealed class UserResource : ResourceType
    {
        private static readonly char[] InvalidSamChars = "\"/\\[]:;|=,+*?<>@".ToCharArray();

        public override string Name => "user";
        public override string ObjectClass => "user";
        public override string RdnType => "CN";
        public override string Description => "User account";
        public override string? LabelAttribute => "sam_account_name";
        public override string Filter => "(&(objectCategory=person)(objectClass=user))";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "userAccountControl", "accountExpires" };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("sam_account_name", AttrType.String)
            {
                Ldap = "sAMAccountName",
                Description = "Logon name (pre-Windows 2000). Defaults to the block label.",
                Required = true,
                Validate = v => ValidateSam(v, 20),
            },
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Object name (the CN). Defaults to display_name, then sam_account_name.",
                DefaultFunc = (get, _) => V.IsEmpty(get("display_name")) ? get("sam_account_name") : get("display_name"),
                Validate = CommonSpecs.NotBlank,
            },
            CommonSpecs.Path("CN=Users"),
            new AttributeSpec("user_principal_name", AttrType.String)
            {
                Ldap = "userPrincipalName",
                Description = "UPN. Defaults to sam_account_name@domain.",
                DefaultFunc = (get, ctx) => get("sam_account_name").IsUnknown ? Value.Unknown : Value.From($"{V.Str(get("sam_account_name"))}@{ctx.Domain}"),
            },
            CommonSpecs.Str("given_name", "givenName", "First name."),
            CommonSpecs.Str("surname", "sn", "Last name."),
            CommonSpecs.Str("initials", "initials", "Initials."),
            new AttributeSpec("display_name", AttrType.String)
            {
                Ldap = "displayName",
                Description = "Display name. Defaults to \"given_name surname\" when either is set.",
                DefaultFunc = (get, _) => FullName(get),
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("email", "mail", "E-mail address."),
            CommonSpecs.Str("title", "title", "Job title."),
            CommonSpecs.Str("department", "department", "Department."),
            CommonSpecs.Str("company", "company", "Company."),
            CommonSpecs.Str("office", "physicalDeliveryOfficeName", "Office."),
            CommonSpecs.Str("phone", "telephoneNumber", "Telephone number."),
            CommonSpecs.Str("mobile", "mobile", "Mobile number."),
            CommonSpecs.Str("employee_id", "employeeID", "Employee ID."),
            CommonSpecs.Str("employee_number", "employeeNumber", "Employee number."),
            CommonSpecs.Str("employee_type", "employeeType", "Employee type."),
            CommonSpecs.Str("street_address", "streetAddress", "Street address."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Country(),
            CommonSpecs.Str("home_directory", "homeDirectory", "Home folder UNC path."),
            CommonSpecs.Str("home_drive", "homeDrive", "Home drive letter, e.g. H:."),
            CommonSpecs.Str("script_path", "scriptPath", "Logon script."),
            CommonSpecs.Str("profile_path", "profilePath", "Roaming profile path."),
            CommonSpecs.Str("web_page", "wWWHomePage", "Web page."),
            new AttributeSpec("proxy_addresses", AttrType.StringList)
            {
                Ldap = "proxyAddresses",
                Description = "E-mail proxy addresses (e.g. SMTP:jdoe@corp.com). Authoritative when set.",
            },
            new AttributeSpec("account_expires", AttrType.String)
            {
                Description = "Account expiry date (YYYY-MM-DD, UTC) or \"never\".",
                Validate = v => AccountExpiry.TryParse(V.Str(v)!, out _) ? null : "must be a date (YYYY-MM-DD) or \"never\".",
                Normalize = AccountExpiry.Normalize,
            },
            CommonSpecs.DnLink("manager", "manager", "DN or sAMAccountName of the user's manager."),
            CommonSpecs.Bool("enabled", "Whether the account is enabled.", true),
            CommonSpecs.Bool("password_never_expires", "Set 'Password never expires'.", false),
            new AttributeSpec("initial_password", AttrType.String)
            {
                Description = "Password set when the account is created. Never read back or compared.",
                Sensitive = true,
                CreateOnly = true,
            },
            new AttributeSpec("change_password_at_logon", AttrType.Bool)
            {
                Description = "Require a password change at first logon (applied at creation only).",
                CreateOnly = true,
                Default = Value.False,
            },
            CommonSpecs.Groups(),
        };

        internal static Value FullName(Func<string, Value> get)
        {
            var given = get("given_name");
            var surname = get("surname");
            if (given.IsUnknown || surname.IsUnknown)
            {
                return Value.Unknown;
            }
            string full = $"{V.Str(given)} {V.Str(surname)}".Trim();
            return full.Length == 0 ? Value.Null : Value.From(full);
        }

        internal static string? ValidateSam(Value v, int maxLength)
        {
            string sam = V.Str(v)!;
            if (sam.Length == 0 || sam.Length > maxLength)
            {
                return $"must be 1-{maxLength} characters.";
            }
            if (sam.IndexOfAny(InvalidSamChars) >= 0 || sam.EndsWith('.'))
            {
                return $"'{sam}' contains characters that are not allowed in a logon name.";
            }
            return null;
        }

        public override IEnumerable<string> ValidateCreate(Config.ResourceInstance desired, bool encryptedConnection)
        {
            bool hasPassword = !V.IsEmpty(desired.Get("initial_password"));
            if (V.Bool(desired.Get("enabled")) == true && !hasPassword)
            {
                yield return "an enabled user needs initial_password when it is created (or set enabled = false).";
            }
            if (hasPassword && !encryptedConnection)
            {
                yield return "Active Directory only accepts passwords over an encrypted connection; use protocol = \"ldaps\" or \"starttls\".";
            }
        }

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            long uac = obj.GetLong("userAccountControl") ?? Uac.NormalAccount;
            result["enabled"] = Value.From((uac & Uac.AccountDisable) == 0);
            result["password_never_expires"] = Value.From((uac & Uac.DontExpirePassword) != 0);
            result["account_expires"] = AccountExpiry.FromLdap(obj.Get("accountExpires"));
        }

        private static long ComputeUac(long current, Func<string, Value> get) =>
            Uac.Set(Uac.WithEnabled(current, get("enabled")), Uac.DontExpirePassword, V.Bool(get("password_never_expires")));

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            // Accounts are always created disabled; they are enabled once the password is set.
            long uac = Uac.NormalAccount | Uac.AccountDisable;
            if (V.Bool(desired.Get("password_never_expires")) == true)
            {
                uac |= Uac.DontExpirePassword;
            }
            attributes["userAccountControl"] = new object[] { uac.ToString() };
            if (!V.IsEmpty(desired.Get("account_expires")))
            {
                attributes["accountExpires"] = new object[] { AccountExpiry.ToLdap(V.Str(desired.Get("account_expires"))!) };
            }
        }

        protected override void AfterCreate(ApplyContext ctx, ResourceChange change)
        {
            var desired = change.Desired!;
            try
            {
                var mods = new List<Modification>();
                string? password = V.Str(desired.Get("initial_password"));
                if (!string.IsNullOrEmpty(password))
                {
                    ctx.Directory.Modify(desired.Dn, new[] { Modification.Replace("unicodePwd", EncodePassword(password)) });
                }
                if (V.Bool(desired.Get("change_password_at_logon")) == true)
                {
                    mods.Add(Modification.Replace("pwdLastSet", "0"));
                }
                mods.Add(Modification.Replace("userAccountControl", ComputeUac(Uac.NormalAccount, desired.Get).ToString()));
                ctx.Directory.Modify(desired.Dn, mods);
            }
            catch (DirectoryException ex)
            {
                // Don't leave a half-configured account behind; the next apply will create it again.
                ctx.Logger.LogWarning("Configuring new user {Dn} failed; removing it again.", desired.Dn);
                try
                {
                    ctx.Directory.Delete(desired.Dn);
                }
                catch (DirectoryException deleteError)
                {
                    ctx.Logger.LogError("Could not remove partially created user {Dn}: {Error}", desired.Dn, deleteError.Message);
                }
                throw PasswordHint(ex);
            }
        }

        private static DirectoryException PasswordHint(DirectoryException ex)
        {
            if (ex.Message.Contains("0000052D", StringComparison.Ordinal) || ex.ResultCode == LdapResult.ConstraintViolation)
            {
                return new DirectoryException($"{ex.Message}\nThe password does not meet the domain password policy (length, complexity or history).", ex.ResultCode, ex);
            }
            if (ex.ResultCode == LdapResult.UnwillingToPerform)
            {
                return new DirectoryException($"{ex.Message}\nSetting passwords requires an encrypted (LDAPS/StartTLS) connection and a password that satisfies policy.", ex.ResultCode, ex);
            }
            return ex;
        }

        public static byte[] EncodePassword(string password) => Encoding.Unicode.GetBytes($"\"{password}\"");

        protected override IEnumerable<Modification> SpecialModifications(ResourceChange change)
        {
            if (change.Get("enabled") != null || change.Get("password_never_expires") != null)
            {
                long current = change.Actual!.GetLong("userAccountControl") ?? Uac.NormalAccount;
                yield return Modification.Replace("userAccountControl", ComputeUac(current, change.Desired!.Get).ToString());
            }
            if (change.Get("account_expires") is { } expiry)
            {
                yield return Modification.Replace("accountExpires", AccountExpiry.ToLdap(V.Str(expiry.New) ?? AccountExpiry.Never));
            }
        }
    }

    /// <summary>accountExpires is a FILETIME; 0 and 0x7FFFFFFFFFFFFFFF both mean "never".</summary>
    internal static class AccountExpiry
    {
        public const string Never = "never";

        public static bool TryParse(string text, out DateTime? date)
        {
            date = null;
            if (text.Equals(Never, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            {
                date = d;
                return true;
            }
            return false;
        }

        public static Value Normalize(Value v) =>
            v is StringValue s && TryParse(s.V, out var d) ? Value.From(d == null ? Never : d.Value.ToString("yyyy-MM-dd")) : v;

        public static string ToLdap(string text) =>
            TryParse(text, out var d) && d != null ? d.Value.ToFileTimeUtc().ToString() : "9223372036854775807";

        public static Value FromLdap(string? raw)
        {
            if (raw == null || !long.TryParse(raw, out long ft) || ft == 0 || ft == long.MaxValue)
            {
                return Value.From(Never);
            }
            return Value.From(DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-dd"));
        }
    }
}
