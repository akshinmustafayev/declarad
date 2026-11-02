using System.Text;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;
using Microsoft.Extensions.Logging;

namespace Declarad.Resources
{
    public sealed class UserResource : ResourceType
    {
        private static readonly char[] InvalidSamChars = "\"/\\[]:;|=,+*?<>@".ToCharArray();

        public override string Name => "user";
        public override string ObjectClass => "user";
        public override string RdnType => "CN";
        public override string Description => "User account";
        public override string? NameAttribute => "sam_account_name";
        public override string Filter => "(&(objectCategory=person)(objectClass=user))";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "userAccountControl", "accountExpires", "userWorkstations" };

        /// <summary>Account options stored as userAccountControl flags. Unmanaged unless set.</summary>
        private static readonly (string Name, long Flag, string Description)[] AccountOptions =
        {
            ("smartcard_required", Uac.SmartcardRequired, "Smart card is required for interactive logon."),
            ("password_not_required", Uac.PasswordNotRequired, "The account may have an empty password."),
            ("reversible_password_encryption", Uac.EncryptedTextPasswordAllowed, "Store the password using reversible encryption."),
            ("trusted_for_delegation", Uac.TrustedForDelegation, "Trusted for Kerberos (unconstrained) delegation."),
            ("trusted_to_auth_for_delegation", Uac.TrustedToAuthForDelegation, "Trusted to authenticate for delegation (protocol transition)."),
            ("account_is_sensitive", Uac.NotDelegated, "Account is sensitive and cannot be delegated."),
            ("use_des_key_only", Uac.UseDesKeyOnly, "Use only Kerberos DES encryption types."),
            ("dont_require_preauth", Uac.DontRequirePreauth, "Do not require Kerberos preauthentication."),
        };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("sam_account_name", AttrType.String)
            {
                Ldap = "sAMAccountName",
                Description = "Logon name (pre-Windows 2000).",
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
            CommonSpecs.Str("middle_name", "middleName", "Middle name."),
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
            CommonSpecs.Str("division", "division", "Division."),
            CommonSpecs.Str("office", "physicalDeliveryOfficeName", "Office."),
            CommonSpecs.Str("phone", "telephoneNumber", "Telephone number."),
            CommonSpecs.List("other_phones", "otherTelephone", "Other telephone numbers."),
            CommonSpecs.Str("mobile", "mobile", "Mobile number."),
            CommonSpecs.List("other_mobiles", "otherMobile", "Other mobile numbers."),
            CommonSpecs.Str("home_phone", "homePhone", "Home telephone number."),
            CommonSpecs.List("other_home_phones", "otherHomePhone", "Other home telephone numbers."),
            CommonSpecs.Str("pager", "pager", "Pager number."),
            CommonSpecs.List("other_pagers", "otherPager", "Other pager numbers."),
            CommonSpecs.Str("fax", "facsimileTelephoneNumber", "Fax number."),
            CommonSpecs.List("other_faxes", "otherFacsimileTelephoneNumber", "Other fax numbers."),
            CommonSpecs.Str("ip_phone", "ipPhone", "IP phone number."),
            CommonSpecs.List("other_ip_phones", "otherIpPhone", "Other IP phone numbers."),
            CommonSpecs.Str("notes", "info", "Notes (the Telephones tab's Notes field)."),
            CommonSpecs.Str("employee_id", "employeeID", "Employee ID."),
            CommonSpecs.Str("employee_number", "employeeNumber", "Employee number."),
            CommonSpecs.Str("employee_type", "employeeType", "Employee type."),
            CommonSpecs.Str("street_address", "streetAddress", "Street address."),
            CommonSpecs.Str("po_box", "postOfficeBox", "P.O. box."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Country(),
            CommonSpecs.Str("country_name", "co", "Country name as shown in ADUC (e.g. Germany). Set it together with country and country_code."),
            CommonSpecs.Number("country_code", "countryCode", "ISO 3166 numeric country code (e.g. 276 for Germany)."),
            CommonSpecs.Str("home_directory", "homeDirectory", "Home folder UNC path."),
            CommonSpecs.Str("home_drive", "homeDrive", "Home drive letter, e.g. H:."),
            CommonSpecs.Str("script_path", "scriptPath", "Logon script."),
            CommonSpecs.Str("profile_path", "profilePath", "Roaming profile path."),
            CommonSpecs.Str("web_page", "wWWHomePage", "Web page."),
            CommonSpecs.List("other_web_pages", "url", "Other web pages."),
            CommonSpecs.Str("preferred_language", "preferredLanguage", "Preferred language, e.g. en-US."),
            new AttributeSpec("logon_workstations", AttrType.StringList)
            {
                Description = "Computers the user may log on to (\"Log On To...\"). Authoritative when set; [] allows all computers.",
                Validate = v => V.List(v).Any(w => w.Contains(',') || string.IsNullOrWhiteSpace(w)) ? "computer names must not be empty or contain commas." : null,
            },
            CommonSpecs.List("service_principal_names", "servicePrincipalName", "SPNs (for service accounts that are users). Authoritative when set."),
            CommonSpecs.List("alt_security_identities", "altSecurityIdentities", "Certificate and Kerberos name mappings (e.g. X509:<I>...<S>...). Authoritative when set."),
            CommonSpecs.Str("mail_nickname", "mailNickname", "Exchange alias (requires the Exchange schema)."),
            new AttributeSpec("hide_from_address_lists", AttrType.Bool)
            {
                Ldap = "msExchHideFromAddressLists",
                Format = LdapFormat.Bool,
                Description = "Hide from Exchange address lists (requires the Exchange schema).",
            },
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
            CommonSpecs.Number("uid_number", "uidNumber", "Unix user ID (RFC 2307)."),
            CommonSpecs.Number("gid_number", "gidNumber", "Unix primary group ID (RFC 2307)."),
            CommonSpecs.Str("unix_home_directory", "unixHomeDirectory", "Unix home directory (RFC 2307)."),
            CommonSpecs.Str("login_shell", "loginShell", "Unix login shell (RFC 2307)."),
        }
        .Concat(AccountOptions.Select(o => new AttributeSpec(o.Name, AttrType.Bool) { Description = o.Description + " Unmanaged unless set." }))
        .Concat(Enumerable.Range(1, 15).Select(i => CommonSpecs.Str($"extension_attribute_{i}", $"extensionAttribute{i}", $"Custom attribute {i} (requires the Exchange schema).")))
        .Append(CommonSpecs.Groups())
        .ToArray();

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
            foreach (var option in AccountOptions)
            {
                result[option.Name] = Value.From((uac & option.Flag) != 0);
            }
            result["logon_workstations"] = Value.FromStrings(SplitWorkstations(obj.Get("userWorkstations")));
            result["account_expires"] = AccountExpiry.FromLdap(obj.Get("accountExpires"));
        }

        private static long ComputeUac(long current, Func<string, Value> get)
        {
            long uac = Uac.Set(Uac.WithEnabled(current, get("enabled")), Uac.DontExpirePassword, V.Bool(get("password_never_expires")));
            foreach (var option in AccountOptions)
            {
                uac = Uac.Set(uac, option.Flag, V.Bool(get(option.Name)));
            }
            return uac;
        }

        private static IEnumerable<string> SplitWorkstations(string? value) =>
            (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
            if (V.List(desired.Get("logon_workstations")) is { Count: > 0 } workstations)
            {
                attributes["userWorkstations"] = new object[] { string.Join(",", workstations) };
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
            if (change.Get("enabled") != null || change.Get("password_never_expires") != null || AccountOptions.Any(o => change.Get(o.Name) != null))
            {
                long current = change.Actual!.GetLong("userAccountControl") ?? Uac.NormalAccount;
                yield return Modification.Replace("userAccountControl", ComputeUac(current, change.Desired!.Get).ToString());
            }
            if (change.Get("account_expires") is { } expiry)
            {
                yield return Modification.Replace("accountExpires", AccountExpiry.ToLdap(V.Str(expiry.New) ?? AccountExpiry.Never));
            }
            if (change.Get("logon_workstations") is { } workstations)
            {
                var names = V.List(workstations.New);
                yield return names.Count == 0 ? Modification.Clear("userWorkstations") : Modification.Replace("userWorkstations", string.Join(",", names));
            }
        }
    }
}
