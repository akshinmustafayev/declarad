using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.DataSources
{
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
}
