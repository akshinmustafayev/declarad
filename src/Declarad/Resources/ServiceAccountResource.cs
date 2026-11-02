using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>
    /// A group Managed Service Account (gMSA). The domain needs a KDS root key (Add-KdsRootKey) before the
    /// first gMSA can be created.
    /// </summary>
    public sealed class ServiceAccountResource : ResourceType
    {
        private const string MembershipAttribute = "msDS-GroupMSAMembership";
        private const string ActOnBehalfAttribute = "msDS-AllowedToActOnBehalfOfOtherIdentity";

        /// <summary>Principal lists stored as security descriptors: attribute name → LDAP attribute.</summary>
        private static readonly Dictionary<string, string> PrincipalLists = new(StringComparer.Ordinal)
        {
            ["principals_allowed_to_retrieve_password"] = MembershipAttribute,
            ["principals_allowed_to_delegate_to_account"] = ActOnBehalfAttribute,
        };

        private static readonly (string Name, long Flag, string Description)[] AccountOptions =
            Uac.OptionsNamed("trusted_for_delegation", "trusted_to_auth_for_delegation", "account_is_sensitive");
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
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "userAccountControl", "msDS-SupportedEncryptionTypes", MembershipAttribute, ActOnBehalfAttribute };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Account name (the CN).",
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
            CommonSpecs.Str("display_name", "displayName", "Display name."),
            CommonSpecs.Str("notes", "info", "Notes."),
            CommonSpecs.DnLink("managed_by", "managedBy", "DN or sAMAccountName of the user or group that manages the account."),
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
            CommonSpecs.List("allowed_to_delegate_to", "msDS-AllowedToDelegateTo", "Kerberos constrained delegation: SPNs of the services this account may delegate to. Authoritative when set."),
            new AttributeSpec("principals_allowed_to_delegate_to_account", AttrType.StringList)
            {
                Description = "Resource-based constrained delegation: accounts (DN or sAMAccountName) allowed to delegate to this account's services. Authoritative when set; [] clears it.",
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
            CommonSpecs.Bool("enabled", "Whether the account is enabled.", true),
            CommonSpecs.Protection(null),
        }
        .Concat(Uac.OptionSpecs(AccountOptions))
        .Append(CommonSpecs.Groups())
        .ToArray();

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
            Uac.ReadOptions(uac, AccountOptions, result);
            long types = obj.GetLong("msDS-SupportedEncryptionTypes") ?? 0;
            result["kerberos_encryption_types"] = Value.FromStrings(EncryptionTypes.Where(e => (types & e.Value) != 0).Select(e => e.Key).Order(StringComparer.Ordinal));

            foreach (var (name, attribute) in PrincipalLists)
            {
                var sd = obj.Binary.TryGetValue(attribute, out var values) && values.Length > 0 ? values[0] : null;
                var principals = SecurityDescriptor.AllowedSids(sd)
                    .Select(sid => directory.GetByDn($"<SID={sid}>", Array.Empty<string>())?.Dn ?? sid)
                    .ToList();
                result[name] = Value.FromStrings(principals);
            }
        }

        private static string EncryptionFlags(Value types) =>
            V.List(types).Aggregate(0L, (acc, t) => acc | EncryptionTypes[t]).ToString();

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            attributes["userAccountControl"] = new object[] { Uac.WithOptions(Uac.WithEnabled(Uac.WorkstationTrust, desired.Get("enabled")), AccountOptions, desired.Get).ToString() };
            attributes["msDS-SupportedEncryptionTypes"] = new object[] { EncryptionFlags(desired.Get("kerberos_encryption_types")) };
            // A gMSA must always carry a membership descriptor, even an empty one.
            attributes[MembershipAttribute] = new object[] { SecurityDescriptor.BuildAllowList(BuiltinAdministrators, Array.Empty<string>(), FullControl) };
        }

        protected override IEnumerable<Modification> SpecialModifications(ResourceChange change)
        {
            if (change.Get("enabled") != null || AccountOptions.Any(o => change.Get(o.Name) != null))
            {
                long current = change.Actual!.GetLong("userAccountControl") ?? Uac.WorkstationTrust;
                long uac = Uac.WithOptions(Uac.WithEnabled(current, change.Desired!.Get("enabled")), AccountOptions, UpdateValues(change));
                yield return Modification.Replace("userAccountControl", uac.ToString());
            }
            if (change.Get("kerberos_encryption_types") != null)
            {
                yield return Modification.Replace("msDS-SupportedEncryptionTypes", EncryptionFlags(change.Desired!.Get("kerberos_encryption_types")));
            }
        }

        public override void ApplyDeferred(ApplyContext ctx, ResourceChange change)
        {
            base.ApplyDeferred(ctx, change);
            foreach (var (name, attribute) in PrincipalLists)
            {
                if (change.Get(name) == null)
                {
                    continue;
                }
                var sids = V.List(change.Desired!.Get(name))
                    .Select(dn => ctx.Directory.GetByDn(dn, Array.Empty<string>())?.Sid
                        ?? throw new DirectoryException($"Cannot resolve the SID of '{dn}'."))
                    .ToList();
                string dn = change.CurrentDn ?? change.TargetDn!;
                // A gMSA always carries a membership descriptor; an empty delegation list removes the attribute.
                var mod = sids.Count == 0 && attribute == ActOnBehalfAttribute
                    ? Modification.Clear(attribute)
                    : new Modification(attribute, ModOp.Replace, new object[] { SecurityDescriptor.BuildAllowList(BuiltinAdministrators, sids, FullControl) });
                ctx.Directory.Modify(dn, new[] { mod });
            }
        }
    }
}
