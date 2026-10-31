using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;
using Microsoft.Extensions.Logging;

namespace Declarad.Resources
{
    public sealed class OuResource : ResourceType
    {
        public const string Protected = "protected_from_accidental_deletion";

        public override string Name => "ou";
        public override string ObjectClass => "organizationalUnit";
        public override string RdnType => "OU";
        public override bool NeedsSecurityDescriptor => true;
        public override string Description => "Organizational unit";

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("OU name (the OU= part of the DN). Defaults to the block label."),
            CommonSpecs.Path(null),
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("display_name", "displayName", "Display name."),
            CommonSpecs.Str("street_address", "street", "Street address."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Country(),
            CommonSpecs.DnLink("managed_by", "managedBy", "DN or sAMAccountName of the managing user or group."),
            new AttributeSpec("block_inheritance", AttrType.Bool)
            {
                Ldap = "gPOptions",
                Format = LdapFormat.IntBool,
                Description = "Block Group Policy inheritance on this OU.",
                Default = Value.False,
            },
            new AttributeSpec(Protected, AttrType.Bool)
            {
                Description = "Deny 'Delete' and 'Delete subtree' to Everyone, like the ADUC checkbox.",
                Default = Value.True,
            },
        };

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            result[Protected] = Value.From(SecurityDescriptor.IsProtectedFromDeletion(obj.SecurityDescriptor));
        }

        public override string? CannotDeleteReason(DirectoryObject actual) =>
            SecurityDescriptor.IsProtectedFromDeletion(actual.SecurityDescriptor)
                ? $"it is protected from accidental deletion. Set {Protected} = false and apply before removing it."
                : null;

        protected override void AfterCreate(ApplyContext ctx, ResourceChange change)
        {
            if (V.Bool(change.Desired!.Get(Protected)) == true)
            {
                SetProtection(ctx, change.Desired!.Dn, true);
            }
        }

        public override void Update(ApplyContext ctx, ResourceChange change)
        {
            // Moving an object needs the Delete right, which protection denies: lift it temporarily.
            bool wasProtected = SecurityDescriptor.IsProtectedFromDeletion(change.Actual!.SecurityDescriptor);
            bool movesParent = !Dn.AreEqual(Dn.Parent(change.CurrentDn!), TargetPath(change.CurrentDn!, change.Desired!));
            if (movesParent && wasProtected)
            {
                SetProtection(ctx, change.CurrentDn!, false);
            }
            base.Update(ctx, change);
            bool want = V.Bool(change.Desired!.Get(Protected)) ?? wasProtected;
            if (change.Get(Protected) != null || (movesParent && wasProtected))
            {
                string dn = $"{RdnType}={Dn.EscapeValue(TargetName(change.CurrentDn!, change.Desired!))},{TargetPath(change.CurrentDn!, change.Desired!)}";
                SetProtection(ctx, dn, want);
            }
        }

        private void SetProtection(ApplyContext ctx, string dn, bool protect)
        {
            var obj = ctx.Directory.GetByDn(dn, Array.Empty<string>(), withSecurityDescriptor: true)
                ?? throw new DirectoryException($"OU '{dn}' not found while updating its protection.");
            if (obj.SecurityDescriptor == null)
            {
                throw new DirectoryException($"Could not read the security descriptor of '{dn}'. The account needs 'Read permissions' on the OU.");
            }
            if (SecurityDescriptor.IsProtectedFromDeletion(obj.SecurityDescriptor) != protect)
            {
                ctx.Directory.SetSecurityDescriptor(dn, SecurityDescriptor.SetProtectedFromDeletion(obj.SecurityDescriptor, protect));
            }
            if (protect)
            {
                ProtectParent(ctx, dn);
            }
        }

        /// <summary>
        /// Like ADUC, protection also denies Everyone "Delete Child" on the parent, so the OU can't be removed through
        /// the parent's rights either. That ACE is never removed again: siblings may rely on it.
        /// </summary>
        private static void ProtectParent(ApplyContext ctx, string dn)
        {
            string parentDn = Dn.Parent(dn);
            try
            {
                var parent = ctx.Directory.GetByDn(parentDn, Array.Empty<string>(), withSecurityDescriptor: true);
                if (parent?.SecurityDescriptor == null || SecurityDescriptor.HasDenyEveryone(parent.SecurityDescriptor, SecurityDescriptor.DeleteChild))
                {
                    return;
                }
                ctx.Directory.SetSecurityDescriptor(parentDn, SecurityDescriptor.SetDenyEveryone(parent.SecurityDescriptor, SecurityDescriptor.DeleteChild, true));
            }
            catch (DeclaradException ex)
            {
                // The object's own deny ACE is the protection that matters; the parent ACE is defence in depth.
                ctx.Logger.LogWarning("Could not add the 'deny Delete Child' ACE to {Parent}: {Error}", parentDn, ex.Message);
            }
        }
    }
}
