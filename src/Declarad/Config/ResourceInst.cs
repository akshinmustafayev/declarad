using Declarad.Hcl.Values;

namespace Declarad.Config
{
    internal sealed class ResourceInst : Instance
    {
        public required ResourceInstance Result { get; init; }
        public Dictionary<string, Value> Memo { get; } = new(StringComparer.Ordinal);
        public HashSet<string> InProgress { get; } = new(StringComparer.Ordinal);
        public ResourceBlock Block => (ResourceBlock)Owner;
    }
}
