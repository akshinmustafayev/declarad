using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;
using Attribute = Declarad.Hcl.Syntax.Attribute;

namespace Declarad.Config
{
    /// <summary>A block that may expand into several instances through for_each or count.</summary>
    internal abstract class Expandable
    {
        public required ModuleScope Scope { get; init; }
        public required Block Block { get; init; }
        public string Label => Block.Labels[^1];
        public Attribute? ForEach => Block.Body.GetAttribute("for_each");
        public Attribute? Count => Block.Body.GetAttribute("count");
        public bool IsExpanded => ForEach != null || Count != null;
        public List<Instance>? Instances { get; set; }
        public bool Expanding { get; set; }
        public bool UnknownExpansion { get; set; }
        public HashSet<string> ExpansionDeps { get; } = new(StringComparer.Ordinal);
        public abstract string Describe { get; }
        public abstract Instance NewInstance(Value? key, Value eachKey, Value eachValue);
    }
}
