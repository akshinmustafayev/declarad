using Declarad.Config;
using Declarad.Directory;
using Declarad.Hcl;
using Declarad.Resources;
using Declarad.State;

namespace Declarad.Engine
{
    public enum ChangeAction
    {
        NoOp,
        Create,
        Update,
        Delete,
        /// <summary>The object no longer exists in the directory; only the state entry is dropped.</summary>
        Forget,
        /// <summary>An attachment moves to a different target: detach from the old one, attach to the new one.</summary>
        Replace,
    }

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

    public sealed class ResourceChange
    {
        public required string Address { get; init; }
        public required ResourceType Type { get; init; }
        public ChangeAction Action { get; set; }

        /// <summary>A delete that runs before creates and updates because it frees a name they need.</summary>
        public bool Early { get; set; }

        /// <summary>The object already exists and is being taken under management by this plan.</summary>
        public bool Adopt { get; init; }

        public ResourceInstance? Desired { get; init; }
        public DirectoryObject? Actual { get; set; }
        public StateResource? Prior { get; init; }
        public List<AttributeChange> Changes { get; } = new();
        public List<string> Notes { get; } = new();

        /// <summary>Addresses that must be created/updated before this one.</summary>
        public HashSet<string> DependsOn { get; } = new(StringComparer.Ordinal);

        public bool IsAttachment => Type is AttachmentType;

        /// <summary>The connection (directory) the change applies to.</summary>
        public string Connection => Desired?.Connection ?? Prior?.Connection ?? StateFile.DefaultConnection;

        public string? CurrentDn => Actual?.Dn;
        public string? TargetDn => Desired?.Dn;
        public bool IsMove => Changes.Any(c => c.Name is "name" or "path");

        public IEnumerable<AttributeChange> ImmediateChanges => Changes.Where(c => !c.Spec.Deferred);
        public IEnumerable<AttributeChange> DeferredChanges => Changes.Where(c => c.Spec.Deferred);

        public AttributeChange? Get(string name) => Changes.FirstOrDefault(c => c.Name == name);
    }

    public sealed class Plan
    {
        public List<ResourceChange> Changes { get; } = new();
        public List<string> Warnings { get; } = new();

        public bool HasChanges => Changes.Any(c => c.Action != ChangeAction.NoOp || c.Adopt);

        public int Count(ChangeAction action) => Changes.Count(c => c.Action == action);

        public int ToAdd => Changes.Count(c => c.Action is ChangeAction.Create or ChangeAction.Replace);
        public int ToChange => Count(ChangeAction.Update);
        public int ToDestroy => Changes.Count(c => c.Action is ChangeAction.Delete or ChangeAction.Replace);
    }
}
