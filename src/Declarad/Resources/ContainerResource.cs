using Declarad.Resources.Schema;

namespace Declarad.Resources
{
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
            CommonSpecs.Name("Container name (the CN)."),
            CommonSpecs.Path(null),
            CommonSpecs.Str("description", "description", "Free-text description."),
        };
    }
}
