using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;

namespace Declarad.Resources
{
    /// <summary>
    /// A group Managed Service Account (gMSA). The domain needs a KDS root key (Add-KdsRootKey) before the
    /// first gMSA can be created.
    /// </summary>
    public sealed class ServiceAccountResource : ResourceType
    {
        private const string MembershipAttribute = "msDS-GroupMSAMembership";
        private const string BuiltinAdministrators = "S-1-5-32-544";
        private const uint FullControl = 0x000F01FF;

        private static readonly Dictionary<string, long> EncryptionTypes = new(StringComparer.Ordinal)
        {
            ["des_cbc_crc"] = 0x1,
            ["des_cbc_md5"] = 0x2,
            ["rc4"] = 0x4,
            ["aes128"] = 0x8,
            ["aes256"] = 0x10,
        };

        public override string Name => "service_account";
        public override string ObjectClass => "msDS-GroupManagedServiceAccount";
        public override string RdnType => "CN";
        public override string Description => "Group managed service account (gMSA)";
        public override string Filter => "(objectClass=msDS-GroupManagedServiceAccount)";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "userAccountControl", "msDS-SupportedEncryptionTypes", MembershipAttribute };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Account name (the CN). Defaults to the block label.",
                Required = true,
                Validate = v => V.Str(v)!.Length > 15 ? "must be at most 15 characters." : CommonSpecs.NotBlank(v),
            },
            CommonSpecs.Path("CN=Managed Service Accounts"),
            new AttributeSpec("sam_account_name", AttrType.String)
            {
                Ldap = "sAMAccountName",
                Description = "Defaults to name followed by $.",
                DefaultFunc = (get, _) => get("name").IsUnknown ? Value.Unknown : Value.From(V.Str(get("name")) + "$"),
                Validate = v => !V.Str(v)!.EndsWith('$') ? "must end with '$'." : UserResource.ValidateSam(v, 16),
            },
            new AttributeSpec("dns_host_name", AttrType.String)
            {
                Ldap = "dNSHostName",
                Description = "DNS host name of the service. Defaults to name.domain.",
                DefaultFunc = (get, ctx) => get("name").IsUnknown ? Value.Unknown : Value.From($"{V.Str(get("name"))!.ToLowerInvariant()}.{ctx.Domain}"),
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            new AttributeSpec("service_principal_names", AttrType.StringList)
            {
                Ldap = "servicePrincipalName",
                Description = "SPNs. Authoritative when set.",
            },
            new AttributeSpec("kerberos_encryption_types", AttrType.StringList)
            {
                Description = "Supported Kerberos encryption types: aes128, aes256, rc4, des_cbc_crc, des_cbc_md5.",
                Default = Value.FromStrings(new[] { "aes128", "aes256" }),
                Validate = v => V.List(v).FirstOrDefault(e => !EncryptionTypes.ContainsKey(e)) is { } bad ? $"'{bad}' is not a known encryption type." : null,
                Normalize = v => v is ListValue l ? Value.FromStrings(V.List(l).Distinct().Order(StringComparer.Ordinal)) : v,
            },
            new AttributeSpec("password_interval_days", AttrType.Number)
            {
                Ldap = "msDS-ManagedPasswordInterval",
                Format = LdapFormat.Int,
                Description = "Password change interval in days. AD only accepts it at creation.",
                Default = Value.From(30),
                CreateOnly = true,
            },
            new AttributeSpec("principals_allowed_to_retrieve_password", AttrType.StringList)
            {
                Description = "Computers or groups (DN or sAMAccountName) allowed to use the account. Authoritative.",
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
            CommonSpecs.Bool("enabled", "Whether the account is enabled.", true),
            CommonSpecs.Groups(),
        };

        public override IEnumerable<string> ValidateCreate(Config.ResourceInstance desired, bool encryptedConnection)
        {
            if (!encryptedConnection)
            {
                yield return "creating a gMSA requires an encrypted connection (ldaps or starttls).";
            }
        }

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            long uac = obj.GetLong("userAccountControl") ?? Uac.WorkstationTrust;
            result["enabled"] = Value.From((uac & Uac.AccountDisable) == 0);
            long types = obj.GetLong("msDS-SupportedEncryptionTypes") ?? 0;
            result["kerberos_encryption_types"] = Value.FromStrings(EncryptionTypes.Where(e => (types & e.Value) != 0).Select(e => e.Key).Order(StringComparer.Ordinal));

            var sd = obj.Binary.TryGetValue(MembershipAttribute, out var values) && values.Length > 0 ? values[0] : null;
            var principals = SecurityDescriptor.AllowedSids(sd)
                .Select(sid => directory.GetByDn($"<SID={sid}>", Array.Empty<string>())?.Dn ?? sid)
                .ToList();
            result["principals_allowed_to_retrieve_password"] = Value.FromStrings(principals);
        }

        private static string EncryptionFlags(Value types) =>
            V.List(types).Aggregate(0L, (acc, t) => acc | EncryptionTypes[t]).ToString();

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            attributes["userAccountControl"] = new object[] { Uac.WithEnabled(Uac.WorkstationTrust, desired.Get("enabled")).ToString() };
            attributes["msDS-SupportedEncryptionTypes"] = new object[] { EncryptionFlags(desired.Get("kerberos_encryption_types")) };
            // A gMSA must always carry a membership descriptor, even an empty one.
            attributes[MembershipAttribute] = new object[] { SecurityDescriptor.BuildAllowList(BuiltinAdministrators, Array.Empty<string>(), FullControl) };
        }

        protected override IEnumerable<Modification> SpecialModifications(ResourceChange change)
        {
            if (change.Get("enabled") != null)
            {
                long current = change.Actual!.GetLong("userAccountControl") ?? Uac.WorkstationTrust;
                yield return Modification.Replace("userAccountControl", Uac.WithEnabled(current, change.Desired!.Get("enabled")).ToString());
            }
            if (change.Get("kerberos_encryption_types") != null)
            {
                yield return Modification.Replace("msDS-SupportedEncryptionTypes", EncryptionFlags(change.Desired!.Get("kerberos_encryption_types")));
            }
        }

        public override void ApplyDeferred(ApplyContext ctx, ResourceChange change)
        {
            base.ApplyDeferred(ctx, change);
            if (change.Get("principals_allowed_to_retrieve_password") == null)
            {
                return;
            }
            var sids = V.List(change.Desired!.Get("principals_allowed_to_retrieve_password"))
                .Select(dn => ctx.Directory.GetByDn(dn, Array.Empty<string>())?.Sid
                    ?? throw new DirectoryException($"Cannot resolve the SID of '{dn}'."))
                .ToList();
            var sd = SecurityDescriptor.BuildAllowList(BuiltinAdministrators, sids, FullControl);
            ctx.Directory.Modify(change.CurrentDn ?? change.TargetDn!, new[] { new Modification(MembershipAttribute, ModOp.Replace, new object[] { sd }) });
        }
    }

    /// <summary>A fine-grained password policy (Password Settings Object).</summary>
    public sealed class PasswordPolicyResource : ResourceType
    {
        public override string Name => "password_policy";
        public override string ObjectClass => "msDS-PasswordSettings";
        public override string RdnType => "CN";
        public override string Description => "Fine-grained password policy";

        private static AttributeSpec Duration(string name, string ldap, string description, string defaultValue) => new(name, AttrType.String)
        {
            Ldap = ldap,
            Format = LdapFormat.Duration,
            Description = description,
            Default = Value.From(defaultValue),
            Validate = AdInterval.Validate,
            Normalize = AdInterval.Normalize,
        };

        /// <summary>The combinations AD itself rejects when the policy is written, reported up front instead.</summary>
        public override IEnumerable<string> Validate(Func<string, Value> get)
        {
            static TimeSpan? Duration(Value v) => AdInterval.TryParse(V.Str(v) ?? "0", out var span) ? span : TimeSpan.Zero;
            static bool IsNever(Value v) => string.Equals(V.Str(v), AdInterval.Never, StringComparison.OrdinalIgnoreCase);

            foreach (var (name, max) in new[] { ("min_password_length", 255L), ("password_history_count", 1024L), ("lockout_threshold", 65535L) })
            {
                if (V.Long(get(name)) is { } n && (n < 0 || n > max))
                {
                    yield return $"{name} must be between 0 and {max}.";
                }
            }
            if (IsNever(get("min_password_age")))
            {
                yield return "min_password_age cannot be \"never\".";
            }
            else if (!IsNever(get("max_password_age")) && Duration(get("min_password_age")) >= Duration(get("max_password_age")))
            {
                yield return "min_password_age must be shorter than max_password_age.";
            }
            if (IsNever(get("lockout_observation_window")))
            {
                yield return "lockout_observation_window cannot be \"never\".";
            }
            else if (!IsNever(get("lockout_duration")) && Duration(get("lockout_observation_window")) > Duration(get("lockout_duration")))
            {
                yield return "lockout_observation_window must not be longer than lockout_duration.";
            }
        }

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("Policy name (the CN). Defaults to the block label."),
            CommonSpecs.Path("CN=Password Settings Container,CN=System"),
            CommonSpecs.Str("description", "description", "Free-text description."),
            new AttributeSpec("precedence", AttrType.Number)
            {
                Ldap = "msDS-PasswordSettingsPrecedence",
                Format = LdapFormat.Int,
                Description = "Lower numbers win when several policies apply.",
                Required = true,
                Validate = v => V.Long(v) < 1 ? "must be at least 1." : null,
            },
            CommonSpecs.Number("min_password_length", "msDS-MinimumPasswordLength", "Minimum password length.", 7),
            CommonSpecs.Number("password_history_count", "msDS-PasswordHistoryLength", "Number of remembered passwords.", 24),
            new AttributeSpec("complexity_enabled", AttrType.Bool)
            {
                Ldap = "msDS-PasswordComplexityEnabled",
                Format = LdapFormat.Bool,
                Description = "Require complex passwords.",
                Default = Value.True,
            },
            new AttributeSpec("reversible_encryption_enabled", AttrType.Bool)
            {
                Ldap = "msDS-PasswordReversibleEncryptionEnabled",
                Format = LdapFormat.Bool,
                Description = "Store passwords with reversible encryption.",
                Default = Value.False,
            },
            Duration("min_password_age", "msDS-MinimumPasswordAge", "Minimum password age.", "1d"),
            Duration("max_password_age", "msDS-MaximumPasswordAge", "Maximum password age, or \"never\".", "42d"),
            CommonSpecs.Number("lockout_threshold", "msDS-LockoutThreshold", "Failed logons before lockout (0 = never lock).", 0),
            Duration("lockout_duration", "msDS-LockoutDuration", "Lockout duration, or \"never\" (until an admin unlocks).", "30m"),
            Duration("lockout_observation_window", "msDS-LockoutObservationWindow", "Window for counting failed logons.", "30m"),
            new AttributeSpec("applies_to", AttrType.StringList)
            {
                Ldap = "msDS-PSOAppliesTo",
                Description = "Users or global security groups (DN or sAMAccountName) the policy applies to. Authoritative.",
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
        };
    }
}
