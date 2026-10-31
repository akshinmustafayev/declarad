using Declarad.Directory;
using Declarad.Hcl;

namespace Declarad.Resources
{
    /// <summary>A read-only lookup (<c>data "type" "name" { ... }</c>), evaluated when the configuration is loaded.</summary>
    public abstract class DataSourceType
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract IReadOnlyList<AttributeSpec> Arguments { get; }

        public AttributeSpec? FindArgument(string name) => Arguments.FirstOrDefault(a => a.Name == name);

        public abstract Value Read(IDirectory directory, Func<string, Value> args);

        protected static ObjectValue Object(IEnumerable<KeyValuePair<string, Value>> fields) => new(fields);
    }

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

    /// <summary>Looks up a Group Policy Object by display name or id.</summary>
    public sealed class GpoDataSource : DataSourceType
    {
        public override string Name => "gpo";
        public override string Description => "Looks up an existing Group Policy Object.";

        public override IReadOnlyList<AttributeSpec> Arguments { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String) { Description = "Display name." },
            new AttributeSpec("id", AttrType.String) { Description = "GPO GUID." },
        };

        public override Value Read(IDirectory directory, Func<string, Value> args)
        {
            string? name = V.Str(args("name"));
            string? id = V.Str(args("id")) is { } raw ? GpoLinkResource.NormalizeGpo(raw) ?? throw new DeclaradException($"data.gpo: '{raw}' is not a GUID.") : null;
            if ((name == null) == (id == null))
            {
                throw new DeclaradException("data.gpo: set exactly one of name or id.");
            }
            string filter = name != null
                ? $"(&(objectClass=groupPolicyContainer)(displayName={Dn.EscapeFilterValue(name)}))"
                : $"(&(objectClass=groupPolicyContainer)(cn={Dn.EscapeFilterValue(id!)}))";
            var found = directory.Search($"CN=Policies,CN=System,{directory.BaseDn}", filter, subtree: false, new[] { "displayName", "gPCFileSysPath", "flags", "versionNumber" });
            if (found.Count != 1)
            {
                throw new DeclaradException(found.Count == 0 ? $"data.gpo: no GPO found with {(name != null ? $"name \"{name}\"" : $"id {id}")}." : $"data.gpo: {found.Count} GPOs are named \"{name}\"; use id.");
            }
            var gpo = found[0];
            return Object(new Dictionary<string, Value>
            {
                ["id"] = Value.From(GpoLinkResource.NormalizeGpo(gpo.Dn)),
                ["dn"] = Value.From(gpo.Dn),
                ["name"] = Value.From(gpo.Get("displayName")),
                ["file_sys_path"] = Value.From(gpo.Get("gPCFileSysPath")),
                ["status"] = Value.From((gpo.GetLong("flags") ?? 0) switch
                {
                    1 => "user_settings_disabled",
                    2 => "computer_settings_disabled",
                    3 => "all_settings_disabled",
                    _ => "all_settings_enabled",
                }),
            });
        }
    }

    /// <summary>Facts about the connected domain.</summary>
    public sealed class DomainDataSource : DataSourceType
    {
        public override string Name => "domain";
        public override string Description => "The connected domain (dn, name, sid).";
        public override IReadOnlyList<AttributeSpec> Arguments { get; } = Array.Empty<AttributeSpec>();

        public override Value Read(IDirectory directory, Func<string, Value> args)
        {
            var root = directory.GetByDn(directory.BaseDn, Array.Empty<string>());
            return Object(new Dictionary<string, Value>
            {
                ["dn"] = Value.From(directory.BaseDn),
                ["name"] = Value.From(directory.DomainName),
                ["sid"] = Value.From(root?.Sid),
            });
        }
    }

    /// <summary>A raw LDAP search returning matching objects.</summary>
    public sealed class ObjectsDataSource : DataSourceType
    {
        public override string Name => "objects";
        public override string Description => "Searches the directory with an LDAP filter.";

        public override IReadOnlyList<AttributeSpec> Arguments { get; } = new[]
        {
            new AttributeSpec("filter", AttrType.String) { Description = "LDAP filter, e.g. (&(objectClass=user)(department=Sales)).", Required = true },
            new AttributeSpec("base", AttrType.String) { Description = "Search base (default: domain root)." },
            new AttributeSpec("scope", AttrType.String) { Description = "subtree (default) or onelevel.", Default = Value.From("subtree"), AllowedValues = new[] { "subtree", "onelevel" } },
            new AttributeSpec("attributes", AttrType.StringList) { Description = "LDAP attributes to return for each object." },
        };

        public override Value Read(IDirectory directory, Func<string, Value> args)
        {
            var attributes = V.List(args("attributes"));
            var found = directory.Search(V.Str(args("base")) ?? directory.BaseDn, V.Str(args("filter"))!, V.Str(args("scope")) != "onelevel", attributes.ToList());
            var objects = found.OrderBy(o => o.Dn, StringComparer.OrdinalIgnoreCase).Select(o =>
            {
                var fields = new Dictionary<string, Value>(StringComparer.Ordinal)
                {
                    ["dn"] = Value.From(o.Dn),
                    ["guid"] = Value.From(o.Guid.ToString()),
                    ["sid"] = Value.From(o.Sid),
                };
                foreach (string a in attributes)
                {
                    var values = o.GetAll(a);
                    // Single-valued attributes become strings; multi-valued ones lists.
                    fields[a] = values.Length switch
                    {
                        0 => Value.Null,
                        1 => Value.From(values[0]),
                        _ => Value.FromStrings(values),
                    };
                }
                return (Value)Object(fields);
            }).ToList();
            return Object(new Dictionary<string, Value>
            {
                ["objects"] = Value.From(objects),
                ["dns"] = Value.FromStrings(found.Select(o => o.Dn).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)),
            });
        }
    }

    public static class DataSourceRegistry
    {
        public static IReadOnlyList<DataSourceType> All { get; } = ResourceRegistry.All
            .Where(t => t is not AttachmentType)
            .Select(t => (DataSourceType)new ObjectDataSource(t))
            .Concat(new DataSourceType[] { new GpoDataSource(), new DomainDataSource(), new ObjectsDataSource() })
            .ToList();

        public static DataSourceType? Find(string name) => All.FirstOrDefault(d => d.Name == name);
    }
}
