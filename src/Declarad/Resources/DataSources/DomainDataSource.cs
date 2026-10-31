using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.DataSources
{
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
}
