using Declarad.Config;
using Declarad.Directory;
using Declarad.Resources;
using Declarad.State;

namespace Declarad.Engine
{
    public sealed class ResourceChange
    {
        public required string Address { get; init; }
        public required ResourceType Type { get; init; }
        public ChangeAction Action { get; set; }

        /// <summary>A delete that runs before creates and updates because it frees a name they need.</summary>
        public bool Early { get; set; }

        /// <summary>The object already exists and is being taken under management by this plan (or under a new address).</summary>
        public bool Adopt { get; init; }

        public ResourceInstance? Desired { get; init; }
        public DirectoryObject? Actual { get; set; }
        public StateResource? Prior { get; init; }
        public List<AttributeChange> Changes { get; } = new();
        public List<string> Notes { get; } = new();

        /// <summary>Addresses that must be created/updated before this one.</summary>
        public HashSet<string> DependsOn { get; } = new(StringComparer.Ordinal);

        public bool IsAttachment => Type is AttachmentType;

        /// <summary>The state moves from another address (renamed block, moved block); the object itself is the same.</summary>
        public bool IsAddressChange => Prior != null && Prior.Address != Address;

        /// <summary>The connection (directory) the change applies to.</summary>
        public string Connection => Desired?.Connection ?? Prior?.Connection ?? StateFile.DefaultConnection;

        public string? CurrentDn => Actual?.Dn;
        public string? TargetDn => Desired?.Dn;
        public bool IsMove => Changes.Any(c => c.Name is "name" or "path");

        public IEnumerable<AttributeChange> ImmediateChanges => Changes.Where(c => !c.Spec.Deferred);
        public IEnumerable<AttributeChange> DeferredChanges => Changes.Where(c => c.Spec.Deferred);

        public AttributeChange? Get(string name) => Changes.FirstOrDefault(c => c.Name == name);
    }
}
