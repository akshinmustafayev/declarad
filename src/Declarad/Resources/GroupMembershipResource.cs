using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>
    /// Adds members to a group declarad does not otherwise manage (e.g. Domain Admins). Additive: only the
    /// listed members are added, and only members this resource added are removed later.
    /// </summary>
    public sealed class GroupMembershipResource : AttachmentType
    {
        public override string Name => "group_membership";
        public override string ObjectClass => "group";
        public override string Description => "Additive members of an existing group";
        public override string TargetAttribute => "group";
        public override IReadOnlyCollection<string> TargetLdapAttributes => new[] { "member" };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("group", AttrType.String)
            {
                Description = "The group (DN or sAMAccountName).",
                Required = true,
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
            new AttributeSpec("members", AttrType.StringList)
            {
                Description = "Members to add (DNs or sAMAccountNames).",
                Required = true,
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
                Additive = true,
            },
        };

        public override Dictionary<string, Value> ReadAttachment(DirectoryObject target, Func<string, Value> identity, IDirectory directory) => new(StringComparer.Ordinal)
        {
            ["group"] = Value.From(target.Dn),
            ["members"] = Value.FromStrings(target.GetAll("member")),
        };

        public override void Attach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            var members = change.Get("members");
            if (members != null)
            {
                ModifyMembers(ctx, target.Dn, "member", members.Added, members.Removed);
            }
        }

        public override void Detach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            var current = target.GetAll("member").Select(Dn.Normalize).ToHashSet(StringComparer.Ordinal);
            var ours = V.List(change.Prior?.Get("members") ?? Value.Null).Where(m => current.Contains(Dn.Normalize(m))).ToList();
            ModifyMembers(ctx, target.Dn, "member", Array.Empty<string>(), ours);
        }
    }
}
