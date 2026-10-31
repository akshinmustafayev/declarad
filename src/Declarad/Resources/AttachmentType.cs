using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>
    /// A resource that manages part of an object it does not own: memberships of an existing group, or a GPO link
    /// on an OU. It never creates or deletes its target; "create" attaches and "delete" detaches. Attachments are
    /// applied after all objects exist.
    /// </summary>
    public abstract class AttachmentType : ResourceType
    {
        public override string RdnType => "";
        public override string? LabelAttribute => null;

        /// <summary>The attribute holding the target object's DN.</summary>
        public abstract string TargetAttribute { get; }

        /// <summary>Attributes whose change means detaching from the old target and attaching anew.</summary>
        public virtual IReadOnlyList<string> IdentityAttributes => new[] { TargetAttribute };

        public override string ComputeDn(Func<string, Value> get, SchemaContext context) => get(TargetAttribute) is StringValue s ? s.V : "";

        /// <summary>The attachment creates its target when missing (e.g. a DNS node) instead of requiring it to exist.</summary>
        public virtual bool CreatesTarget => false;

        public virtual DirectoryObject CreateTarget(ApplyContext ctx, ResourceChange change) =>
            throw new DirectoryException($"Target '{change.Desired!.Dn}' does not exist.");

        public override bool Matches(DirectoryObject obj) => true;

        public abstract IReadOnlyCollection<string> TargetLdapAttributes { get; }

        /// <summary>Current values of this attachment on <paramref name="target"/>; <paramref name="identity"/> gives the attachment's own identity attributes.</summary>
        public abstract Dictionary<string, Value> ReadAttachment(DirectoryObject target, Func<string, Value> identity, IDirectory directory);

        public abstract void Attach(ApplyContext ctx, ResourceChange change, DirectoryObject target);

        public abstract void Detach(ApplyContext ctx, ResourceChange change, DirectoryObject target);

        public override DirectoryObject Create(ApplyContext ctx, ResourceChange change) => throw new InvalidOperationException();
        public override void Update(ApplyContext ctx, ResourceChange change) => throw new InvalidOperationException();
        public override void Delete(ApplyContext ctx, ResourceChange change) => throw new InvalidOperationException();
    }
}
