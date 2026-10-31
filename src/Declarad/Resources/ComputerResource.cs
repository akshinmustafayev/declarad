using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;

namespace Declarad.Resources
{
    /// <summary>A computer account, typically pre-staged before a machine joins the domain.</summary>
    public sealed class ComputerResource : ResourceType
    {
        public override string Name => "computer";
        public override string ObjectClass => "computer";
        public override string RdnType => "CN";
        public override string Description => "Computer account (pre-staged)";
        public override string Filter => "(objectCategory=computer)";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "userAccountControl" };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Computer name (the CN). Defaults to the block label.",
                Required = true,
                Validate = v => V.Str(v)!.Length > 15 ? "must be at most 15 characters (NetBIOS limit)." : CommonSpecs.NotBlank(v),
            },
            CommonSpecs.Path("CN=Computers"),
            new AttributeSpec("sam_account_name", AttrType.String)
            {
                Ldap = "sAMAccountName",
                Description = "Defaults to the upper-cased name followed by $.",
                DefaultFunc = (get, _) => get("name").IsUnknown ? Value.Unknown : Value.From(V.Str(get("name"))!.ToUpperInvariant() + "$"),
                Validate = v => !V.Str(v)!.EndsWith('$') ? "must end with '$'." : UserResource.ValidateSam(v, 16),
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("location", "location", "Location."),
            new AttributeSpec("dns_host_name", AttrType.String)
            {
                Ldap = "dNSHostName",
                Description = "Fully qualified host name. Defaults to name.domain.",
                DefaultFunc = (get, ctx) => get("name").IsUnknown ? Value.Unknown : Value.From($"{V.Str(get("name"))!.ToLowerInvariant()}.{ctx.Domain}"),
            },
            new AttributeSpec("service_principal_names", AttrType.StringList)
            {
                Ldap = "servicePrincipalName",
                Description = "SPNs. Authoritative when set.",
            },
            CommonSpecs.DnLink("managed_by", "managedBy", "DN or sAMAccountName of the managing user or group."),
            CommonSpecs.Bool("enabled", "Whether the account is enabled.", true),
            CommonSpecs.Groups(),
        };

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            long uac = obj.GetLong("userAccountControl") ?? Uac.WorkstationTrust;
            result["enabled"] = Value.From((uac & Uac.AccountDisable) == 0);
        }

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            // Pre-staged accounts get their password when the machine joins.
            long uac = Uac.WithEnabled(Uac.WorkstationTrust | Uac.PasswordNotRequired, desired.Get("enabled"));
            attributes["userAccountControl"] = new object[] { uac.ToString() };
        }

        protected override IEnumerable<Modification> SpecialModifications(ResourceChange change)
        {
            if (change.Get("enabled") != null)
            {
                long current = change.Actual!.GetLong("userAccountControl") ?? Uac.WorkstationTrust;
                yield return Modification.Replace("userAccountControl", Uac.WithEnabled(current, change.Desired!.Get("enabled")).ToString());
            }
        }
    }

    /// <summary>A mail contact (an address book entry that is not a security principal).</summary>
    public sealed class ContactResource : ResourceType
    {
        public override string Name => "contact";
        public override string ObjectClass => "contact";
        public override string RdnType => "CN";
        public override string Description => "Mail contact";

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Object name (the CN). Defaults to the block label.",
                Required = true,
                Validate = CommonSpecs.NotBlank,
            },
            CommonSpecs.Path("CN=Users"),
            CommonSpecs.Str("given_name", "givenName", "First name."),
            CommonSpecs.Str("surname", "sn", "Last name."),
            CommonSpecs.Str("initials", "initials", "Initials."),
            new AttributeSpec("display_name", AttrType.String)
            {
                Ldap = "displayName",
                Description = "Display name. Defaults to \"given_name surname\" when either is set.",
                DefaultFunc = (get, _) => UserResource.FullName(get),
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("email", "mail", "E-mail address."),
            CommonSpecs.Str("title", "title", "Job title."),
            CommonSpecs.Str("department", "department", "Department."),
            CommonSpecs.Str("company", "company", "Company."),
            CommonSpecs.Str("office", "physicalDeliveryOfficeName", "Office."),
            CommonSpecs.Str("phone", "telephoneNumber", "Telephone number."),
            CommonSpecs.Str("mobile", "mobile", "Mobile number."),
            CommonSpecs.Str("street_address", "streetAddress", "Street address."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Country(),
            new AttributeSpec("proxy_addresses", AttrType.StringList)
            {
                Ldap = "proxyAddresses",
                Description = "E-mail proxy addresses. Authoritative when set.",
            },
            CommonSpecs.DnLink("manager", "manager", "DN of the contact's manager."),
            CommonSpecs.Groups(),
        };
    }

    /// <summary>A generic container (CN=...), for layouts that use containers instead of OUs.</summary>
    public sealed class ContainerResource : ResourceType
    {
        public override string Name => "container";
        public override string ObjectClass => "container";
        public override string RdnType => "CN";
        public override string Description => "Container";
        public override string Filter => "(&(objectClass=container)(!(isCriticalSystemObject=TRUE)))";

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("Container name (the CN). Defaults to the block label."),
            CommonSpecs.Path(null),
            CommonSpecs.Str("description", "description", "Free-text description."),
        };
    }
}
