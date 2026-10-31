using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.DataSources
{
    /// <summary>Looks up one existing object of a resource type by dn, guid, sam_account_name or name.</summary>
    public sealed class ObjectDataSource : DataSourceType
    {
        private readonly ResourceType _type;

        public ObjectDataSource(ResourceType type)
        {
            _type = type;
            var args = new List<AttributeSpec>
            {
                new("dn", AttrType.String) { Description = "Distinguished name." },
                new("guid", AttrType.String) { Description = "objectGUID." },
                new("name", AttrType.String) { Description = "Name (CN/OU value), searched under base." },
                new("base", AttrType.String) { Description = "Search base for name/sam_account_name lookups (default: domain root)." },
            };
            if (type.Find("sam_account_name") != null)
            {
                args.Add(new AttributeSpec("sam_account_name", AttrType.String) { Description = "sAMAccountName." });
            }
            Arguments = args;
        }

        public override string Name => _type.Name;
        public override string Description => $"Looks up an existing {Lower(_type.Description)}.";

        /// <summary>Lower-cases the first letter unless the text starts with an acronym ("AD-integrated", "DNS").</summary>
        private static string Lower(string text) =>
            text.Length < 2 || char.IsUpper(text[1]) ? text : char.ToLowerInvariant(text[0]) + text[1..];
        public override IReadOnlyList<AttributeSpec> Arguments { get; }

        public override Value Read(IDirectory directory, Func<string, Value> args)
        {
            var keys = new[] { "dn", "guid", "name", "sam_account_name" }.Where(k => FindArgument(k) != null && !args(k).IsNull).ToList();
            if (keys.Count != 1)
            {
                throw new DeclaradException($"data.{Name}: set exactly one of {string.Join(", ", new[] { "dn", "guid", "name", "sam_account_name" }.Where(k => FindArgument(k) != null))}.");
            }
            string key = keys[0];
            string value = V.Str(args(key))!;
            string searchBase = V.Str(args("base")) ?? _type.DefaultSearchBase(directory);
            var attrs = _type.LdapAttributes;

            DirectoryObject? obj = key switch
            {
                "dn" => directory.GetByDn(value, attrs, _type.NeedsSecurityDescriptor),
                "guid" => Guid.TryParse(value, out var g) ? directory.GetByGuid(g, attrs, _type.NeedsSecurityDescriptor) : throw new DeclaradException($"data.{Name}: '{value}' is not a GUID."),
                _ => Single(directory, searchBase, $"(&{_type.Filter}({(key == "name" ? _type.RdnType.ToLowerInvariant() : "sAMAccountName")}={Dn.EscapeFilterValue(value)}))", attrs, value),
            };
            if (obj == null || !_type.Matches(obj))
            {
                throw new DeclaradException($"data.{Name}: no {Lower(_type.Description)} found with {key} = \"{value}\".");
            }
            if (_type.NeedsSecurityDescriptor && obj.SecurityDescriptor == null)
            {
                obj = directory.GetByDn(obj.Dn, attrs, true) ?? obj;
            }

            var fields = _type.Read(obj, directory)
                .Where(kv => _type.Find(kv.Key) is not { Sensitive: true })
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            fields["dn"] = Value.From(obj.Dn);
            fields["guid"] = Value.From(obj.Guid.ToString());
            fields["sid"] = Value.From(obj.Sid);
            return Object(fields);
        }

        private DirectoryObject? Single(IDirectory directory, string searchBase, string filter, IReadOnlyCollection<string> attrs, string value)
        {
            var found = directory.Search(searchBase, filter, subtree: true, attrs);
            if (found.Count > 1)
            {
                throw new DeclaradException($"data.{Name}: {found.Count} objects match \"{value}\": {string.Join("; ", found.Take(3).Select(f => f.Dn))}. Use dn or narrow base.");
            }
            return found.FirstOrDefault();
        }
    }
}
