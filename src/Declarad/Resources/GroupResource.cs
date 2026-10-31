using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;

namespace Declarad.Resources
{
    public sealed class GroupResource : ResourceType
    {
        private const long GlobalScope = 0x2;
        private const long DomainLocalScope = 0x4;
        private const long UniversalScope = 0x8;
        private const long SecurityEnabled = 0x80000000;

        public override string Name => "group";
        public override string ObjectClass => "group";
        public override string RdnType => "CN";
        public override string Description => "Security or distribution group";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "groupType" };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("Group name (the CN). Defaults to the block label."),
            CommonSpecs.Path("CN=Users"),
            new AttributeSpec("sam_account_name", AttrType.String)
            {
                Ldap = "sAMAccountName",
                Description = "Pre-Windows 2000 name. Defaults to name.",
                DefaultFunc = (get, _) => get("name"),
                Validate = v => V.Str(v)!.Length > 256 ? "must be at most 256 characters." : null,
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("display_name", "displayName", "Display name."),
            CommonSpecs.Str("email", "mail", "E-mail address."),
            CommonSpecs.Str("notes", "info", "Notes."),
            new AttributeSpec("scope", AttrType.String)
            {
                Description = "global, domain_local or universal.",
                Default = Value.From("global"),
                AllowedValues = new[] { "global", "domain_local", "universal" },
            },
            new AttributeSpec("category", AttrType.String)
            {
                Description = "security or distribution.",
                Default = Value.From("security"),
                AllowedValues = new[] { "security", "distribution" },
            },
            CommonSpecs.DnLink("managed_by", "managedBy", "DN or sAMAccountName of the managing user or group."),
            new AttributeSpec("members", AttrType.StringList)
            {
                Ldap = "member",
                Description = "Authoritative member list (DNs or sAMAccountNames). Omit to leave membership unmanaged.",
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
            CommonSpecs.Groups(),
        };

        public static long GroupType(string scope, string category)
        {
            long type = scope switch
            {
                "domain_local" => DomainLocalScope,
                "universal" => UniversalScope,
                _ => GlobalScope,
            };
            if (category == "security")
            {
                type |= SecurityEnabled;
            }
            // groupType is a signed 32-bit integer in AD.
            return (int)(uint)type;
        }

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            long type = obj.GetLong("groupType") ?? 0;
            result["scope"] = Value.From((type & DomainLocalScope) != 0 ? "domain_local" : (type & UniversalScope) != 0 ? "universal" : "global");
            result["category"] = Value.From((type & SecurityEnabled) != 0 ? "security" : "distribution");
        }

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            attributes["groupType"] = new object[] { GroupType(V.Str(desired.Get("scope"))!, V.Str(desired.Get("category"))!).ToString() };
        }

        protected override void AfterUpdate(ApplyContext ctx, ResourceChange change, string dn)
        {
            if (change.Get("scope") == null && change.Get("category") == null)
            {
                return;
            }
            string fromScope = V.Str(change.Get("scope")?.Old ?? change.Desired!.Get("scope"))!;
            string toScope = V.Str(change.Desired!.Get("scope"))!;
            string category = V.Str(change.Desired!.Get("category"))!;
            // AD forbids converting directly between global and domain-local; go through universal.
            if (fromScope != toScope && fromScope != "universal" && toScope != "universal")
            {
                ctx.Directory.Modify(dn, new[] { Modification.Replace("groupType", GroupType("universal", category).ToString()) });
            }
            ctx.Directory.Modify(dn, new[] { Modification.Replace("groupType", GroupType(toScope, category).ToString()) });
        }
    }
}
