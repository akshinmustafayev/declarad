using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.DataSources
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
}
