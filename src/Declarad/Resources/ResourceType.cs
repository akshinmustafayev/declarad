using System.Globalization;
using Declarad.Config;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;
using Microsoft.Extensions.Logging;

namespace Declarad.Resources
{
    /// <summary>
    /// A kind of directory object declarad manages. The base class implements the generic parts:
    /// typed attribute mapping, rename/move, authoritative multi-valued attributes, additive group
    /// membership (<c>groups</c>) and delete. Subclasses add type-specific behaviour.
    /// Every object type has a <c>name</c> (the RDN value) and a <c>path</c> (the parent container DN).
    /// </summary>
    public abstract class ResourceType
    {
        private Dictionary<string, AttributeSpec>? _byName;

        public abstract string Name { get; }
        public abstract string ObjectClass { get; }
        public abstract string RdnType { get; }
        public abstract IReadOnlyList<AttributeSpec> Schema { get; }
        public virtual string Description => "";

        /// <summary>
        /// The attribute that names the object (a user's logon name, otherwise its name). Export labels the blocks it
        /// generates after it. Block labels are only addresses in declarad; they are never used as values.
        /// </summary>
        public virtual string? NameAttribute => "name";

        public virtual bool NeedsSecurityDescriptor => false;

        /// <summary>LDAP filter selecting objects of this type for export and data sources.</summary>
        public virtual string Filter => $"(objectClass={ObjectClass})";

        /// <summary>Where objects of this type are searched for by default (export, data sources).</summary>
        public virtual string DefaultSearchBase(IDirectory directory) => directory.BaseDn;

        /// <summary>Additional LDAP attributes needed by <see cref="ReadSpecial"/>.</summary>
        protected virtual IEnumerable<string> ExtraLdapAttributes => Array.Empty<string>();

        public AttributeSpec? Find(string name)
        {
            _byName ??= Schema.ToDictionary(s => s.Name, StringComparer.Ordinal);
            return _byName.GetValueOrDefault(name);
        }

        public IReadOnlyCollection<string> LdapAttributes =>
            Schema.Where(s => s.Ldap != null).Select(s => s.Ldap!)
                .Concat(Find("groups")?.Additive == true ? new[] { "memberOf" } : Array.Empty<string>())
                .Concat(ExtraLdapAttributes)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Canonicalizes evaluated attributes that depend on each other (e.g. DNS names relative to a zone).</summary>
        public virtual void Normalize(Config.ResourceInstance instance)
        {
        }

        public virtual string ComputeDn(Func<string, Value> get, SchemaContext context)
        {
            var name = get("name");
            var path = get("path");
            if (name.IsUnknown || path.IsUnknown)
            {
                return "";
            }
            return Dn.Combine(RdnType, V.Str(name) ?? throw new DeclaradException($"{Name}: name is not set."), V.Str(path) ?? throw new DeclaradException($"{Name}: path is not set."));
        }

        /// <summary>Type-specific validation of the fully evaluated attributes; returns error messages.</summary>
        public virtual IEnumerable<string> Validate(Func<string, Value> get) => Array.Empty<string>();

        /// <summary>Checks that apply only when the object is about to be created.</summary>
        public virtual IEnumerable<string> ValidateCreate(ResourceInstance desired, bool encryptedConnection) => Array.Empty<string>();

        /// <summary>Whether a directory object is of this type (by structural class, so a computer is not a user).</summary>
        public virtual bool Matches(DirectoryObject obj) => obj.HasStructuralClass(ObjectClass);

        // ---- Reading ---------------------------------------------------------------------------------

        /// <summary>Converts a directory object to schema attribute values (create-only attributes are skipped).</summary>
        public Dictionary<string, Value> Read(DirectoryObject obj, IDirectory directory)
        {
            var result = new Dictionary<string, Value>(StringComparer.Ordinal);
            var rdn = Dn.First(obj.Dn);
            result["name"] = Value.From(rdn.Value);
            result["path"] = Value.From(Dn.Parent(obj.Dn));
            foreach (var spec in Schema.Where(s => s.Ldap != null && !s.CreateOnly))
            {
                result[spec.Name] = FromLdap(spec, obj.GetAll(spec.Ldap!));
            }
            if (Find("groups") is { Additive: true })
            {
                result["groups"] = Value.FromStrings(obj.GetAll("memberOf"));
            }
            ReadSpecial(obj, result, directory);
            return result;
        }

        protected virtual void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
        }

        public static Value FromLdap(AttributeSpec spec, string[] values)
        {
            if (spec.Type == AttrType.StringList)
            {
                return Value.FromStrings(values);
            }
            string? first = values.Length > 0 ? values[0] : null;
            switch (spec.Format)
            {
                case LdapFormat.Bool:
                    return first == null ? Value.Null : Value.From(first.Equals("TRUE", StringComparison.OrdinalIgnoreCase));
                case LdapFormat.IntBool:
                    return Value.From(first != null && first != "0");
                case LdapFormat.Int:
                    return first == null ? Value.Null : Value.From(decimal.Parse(first, CultureInfo.InvariantCulture));
                case LdapFormat.Duration:
                    return first == null ? Value.Null : Value.From(AdInterval.FromLdap(first));
                default:
                    return spec.Type == AttrType.Number && first != null
                        ? Value.From(decimal.Parse(first, CultureInfo.InvariantCulture))
                        : Value.From(first);
            }
        }

        public static object[] ToLdap(AttributeSpec spec, Value value) => value switch
        {
            ListValue l => l.Items.Select(i => (object)V.Str(i)!).ToArray(),
            _ => new object[]
            {
                spec.Format switch
                {
                    LdapFormat.Bool => V.Bool(value) == true ? "TRUE" : "FALSE",
                    LdapFormat.IntBool => V.Bool(value) == true ? "1" : "0",
                    LdapFormat.Int => V.Long(value)!.Value.ToString(CultureInfo.InvariantCulture),
                    LdapFormat.Duration => AdInterval.ToLdap(V.Str(value)!),
                    _ => V.Str(value)!,
                },
            },
        };

        protected static bool IsEmptyValue(Value value) => V.IsEmpty(value) || (value is ListValue l && l.Items.Length == 0);

        protected static Modification ToModification(AttributeSpec spec, Value value) =>
            IsEmptyValue(value) && spec.Format != LdapFormat.IntBool
                ? Modification.Clear(spec.Ldap!)
                : new Modification(spec.Ldap!, ModOp.Replace, ToLdap(spec, value));

        /// <summary>Why the object cannot be deleted as-is, or null.</summary>
        public virtual string? CannotDeleteReason(DirectoryObject actual) => null;

        // ---- Apply -------------------------------------------------------------------------------

        public virtual DirectoryObject Create(ApplyContext ctx, ResourceChange change)
        {
            var desired = change.Desired!;
            var attributes = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["objectClass"] = new object[] { ObjectClass },
            };
            foreach (var spec in Schema.Where(s => s.Ldap != null && !s.Deferred))
            {
                var value = desired.Get(spec.Name);
                if (IsEmptyValue(value))
                {
                    continue;
                }
                attributes[spec.Ldap!] = ToLdap(spec, value);
            }
            AddCreateAttributes(desired, attributes);
            ctx.Logger.LogDebug("Creating {Dn}", desired.Dn);
            ctx.Directory.Add(desired.Dn, attributes);
            AfterCreate(ctx, change);
            return ReadBack(ctx, desired.Dn);
        }

        protected virtual void AddCreateAttributes(ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
        }

        protected virtual void AfterCreate(ApplyContext ctx, ResourceChange change)
        {
        }

        /// <summary>
        /// Compares against the object's current DN (re-read before applying), so an object that already
        /// moved because its parent OU was renamed is not moved again. Name comparison is case-sensitive
        /// so case-only renames are applied.
        /// </summary>
        public static bool NeedsMove(string currentDn, ResourceInstance desired) =>
            Dn.First(currentDn).Value != TargetName(currentDn, desired)
            || !Dn.AreEqual(Dn.Parent(currentDn), TargetPath(currentDn, desired));

        /// <summary>The name to apply: the configured one, unless name is in ignore_changes.</summary>
        public static string TargetName(string currentDn, ResourceInstance desired) =>
            desired.IgnoreChanges.Contains("name") ? Dn.First(currentDn).Value : V.Str(desired.Get("name"))!;

        /// <summary>The parent to apply: the configured path, unless path is in ignore_changes.</summary>
        public static string TargetPath(string currentDn, ResourceInstance desired) =>
            desired.IgnoreChanges.Contains("path") ? Dn.Parent(currentDn) : V.Str(desired.Get("path"))!;

        /// <summary>Phase 1 of an update: rename/move, then immediate attribute changes.</summary>
        public virtual void Update(ApplyContext ctx, ResourceChange change)
        {
            string dn = change.CurrentDn!;
            var desired = change.Desired!;
            if (NeedsMove(dn, desired))
            {
                string newRdn = $"{RdnType}={Dn.EscapeValue(TargetName(dn, desired))}";
                string newParent = TargetPath(dn, desired);
                ctx.Logger.LogDebug("Renaming {Old} to {Rdn},{Parent}", dn, newRdn, newParent);
                ctx.Directory.Rename(dn, newRdn, newParent);
                dn = $"{newRdn},{newParent}";
            }

            var mods = new List<Modification>();
            foreach (var c in change.ImmediateChanges.Where(c => c.Spec.Ldap != null))
            {
                mods.Add(ToModification(c.Spec, c.New));
            }
            mods.AddRange(SpecialModifications(change));
            if (mods.Count > 0)
            {
                ctx.Directory.Modify(dn, mods);
            }
            AfterUpdate(ctx, change, dn);
        }

        protected virtual IEnumerable<Modification> SpecialModifications(ResourceChange change) => Array.Empty<Modification>();

        protected virtual void AfterUpdate(ApplyContext ctx, ResourceChange change, string dn)
        {
        }

        /// <summary>Phase 2: deferred attributes (memberships, manager links), applied once every object exists.</summary>
        public virtual void ApplyDeferred(ApplyContext ctx, ResourceChange change)
        {
            // The applier re-read the object, so this is its real DN (it differs from the configured one when
            // name or path is in ignore_changes).
            string dn = change.CurrentDn ?? change.TargetDn!;
            var single = change.DeferredChanges
                .Where(c => c.Spec.Ldap != null && c.Spec.Type != AttrType.StringList)
                .Select(c => ToModification(c.Spec, c.New))
                .ToList();
            if (single.Count > 0)
            {
                ctx.Directory.Modify(dn, single);
            }
            foreach (var c in change.DeferredChanges.Where(c => c.Spec.Ldap != null && c.Spec.Type == AttrType.StringList && !c.Spec.Additive))
            {
                ModifyMembers(ctx, dn, c.Spec.Ldap!, c.Added, c.Removed);
            }
            if (change.Get("groups") is { Spec.Additive: true } groups)
            {
                foreach (string group in groups.Removed)
                {
                    TolerantMemberChange(ctx, group, new Modification("member", ModOp.Delete, new object[] { dn }));
                }
                foreach (string group in groups.Added)
                {
                    TolerantMemberChange(ctx, group, new Modification("member", ModOp.Add, new object[] { dn }));
                }
            }
        }

        public virtual void Delete(ApplyContext ctx, ResourceChange change)
        {
            ctx.Logger.LogDebug("Deleting {Dn}", change.CurrentDn);
            ctx.Directory.Delete(change.CurrentDn!);
        }

        protected DirectoryObject ReadBack(ApplyContext ctx, string dn) =>
            ctx.Directory.GetByDn(dn, LdapAttributes, NeedsSecurityDescriptor)
            ?? throw new DirectoryException($"Object '{dn}' was not found after it was written.");

        /// <summary>Applies value additions/removals to a multi-valued DN attribute in chunks, tolerating no-op errors.</summary>
        protected static void ModifyMembers(ApplyContext ctx, string dn, string attribute, IReadOnlyList<string> add, IReadOnlyList<string> remove)
        {
            const int chunk = 500;
            foreach (var batch in remove.Chunk(chunk))
            {
                try
                {
                    ctx.Directory.Modify(dn, new[] { new Modification(attribute, ModOp.Delete, batch) });
                }
                catch (DirectoryException ex) when (ex.ResultCode is LdapResult.UnwillingToPerform or LdapResult.NoSuchAttribute)
                {
                    // Some values were already gone (e.g. a member renamed meanwhile); retry one by one.
                    foreach (string value in batch)
                    {
                        TolerantMemberChange(ctx, dn, new Modification(attribute, ModOp.Delete, new object[] { value }));
                    }
                }
            }
            foreach (var batch in add.Chunk(chunk))
            {
                try
                {
                    ctx.Directory.Modify(dn, new[] { new Modification(attribute, ModOp.Add, batch) });
                }
                catch (DirectoryException ex) when (ex.ResultCode is LdapResult.EntryAlreadyExists or LdapResult.AttributeOrValueExists)
                {
                    foreach (string value in batch)
                    {
                        TolerantMemberChange(ctx, dn, new Modification(attribute, ModOp.Add, new object[] { value }));
                    }
                }
            }
        }

        protected static void TolerantMemberChange(ApplyContext ctx, string dn, Modification mod)
        {
            try
            {
                ctx.Directory.Modify(dn, new[] { mod });
            }
            catch (DirectoryException ex) when (ex.ResultCode is LdapResult.EntryAlreadyExists or LdapResult.AttributeOrValueExists
                                                   || (mod.Op == ModOp.Delete && ex.ResultCode is LdapResult.UnwillingToPerform or LdapResult.NoSuchAttribute))
            {
                ctx.Logger.LogDebug("{Attribute} change on {Dn} was already in effect: {Error}", mod.Attribute, dn, ex.Message);
            }
        }
    }
}
