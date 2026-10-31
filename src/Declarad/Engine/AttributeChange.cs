using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Engine
{
    public sealed record AttributeChange(AttributeSpec Spec, Value Old, Value New)
    {
        public string Name => Spec.Name;

        /// <summary>For list attributes: values to add (normalized DNs or strings).</summary>
        public IReadOnlyList<string> Added { get; init; } = Array.Empty<string>();

        /// <summary>For list attributes: values to remove.</summary>
        public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();

        /// <summary>objectGUIDs of removed DNs, to follow objects renamed before the removal is applied.</summary>
        public IReadOnlyList<Guid> RemovedGuids { get; init; } = Array.Empty<Guid>();
    }
}
