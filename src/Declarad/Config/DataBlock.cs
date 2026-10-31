using Declarad.Hcl.Values;
using Declarad.Resources.DataSources;

namespace Declarad.Config
{
    internal sealed class DataBlock : Expandable
    {
        public required DataSourceType Type { get; init; }
        public override string Describe => (Scope.Prefix.Length > 0 ? Scope.Prefix + "." : "") + $"data.{Type.Name}.{Label}";
        public override Instance NewInstance(Value? key, Value eachKey, Value eachValue) => new DataInst { Owner = this, Key = key, EachKey = eachKey, EachValue = eachValue };
    }
}
