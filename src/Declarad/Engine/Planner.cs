using Declarad.Config;
using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.Schema;
using Declarad.State;

namespace Declarad.Engine
{
    /// <summary>
    /// Compares the configuration with the directory (located through state by objectGUID) and produces an
    /// ordered <see cref="Plan"/>: creates and updates in dependency order, then deletes (attachments first,
    /// then objects deepest DN first).
    /// </summary>
    public sealed class Planner
    {
        private readonly Func<string, IDirectory> _directories;
        private readonly Configuration _config;
        private readonly StateFile _state;
        private readonly PlanOptions _options;
        private readonly List<DeclaradException> _errors = new();
        private readonly Dictionary<string, string?> _samCache = new(StringComparer.OrdinalIgnoreCase);

        public Planner(IDirectory directory, Configuration config, StateFile state, PlanOptions options)
            : this(_ => directory, config, state, options)
        {
        }

        /// <param name="directories">The directory of each connection name.</param>
        public Planner(Func<string, IDirectory> directories, Configuration config, StateFile state, PlanOptions options)
        {
            _directories = directories;
            _config = config;
            _state = state;
            _options = options;
        }

        private readonly Dictionary<string, IDirectory> _opened = new(StringComparer.Ordinal);

        private IDirectory Dir(string connection)
        {
            if (!_opened.TryGetValue(connection, out var directory))
            {
                directory = _directories(connection);
                _opened[connection] = directory;
            }
            return directory;
        }

        private SchemaContext Schema(string connection) =>
            _config.Connections.TryGetValue(connection, out var c) ? c.Schema : new SchemaContext("", "");

        private static bool MatchesTarget(string address, IEnumerable<string> targets) =>
            targets.Any(t => address == t || address.StartsWith(t + "[", StringComparison.Ordinal) || address.StartsWith(t + ".", StringComparison.Ordinal));

        /// <summary>
        /// The addresses a --target plan covers, like Terraform: the targets plus everything they depend on, or
        /// for destroy, the targets plus everything that depends on them. Null means no targeting.
        /// </summary>
        private HashSet<string>? TargetClosure()
        {
            if (_options.Targets.Count == 0)
            {
                return null;
            }
            var resources = _config.Resources.ToDictionary(r => r.Address, StringComparer.Ordinal);
            var edges = resources.Values.ToDictionary(r => r.Address, r => new HashSet<string>(r.References.Concat(r.DependsOn), StringComparer.Ordinal), StringComparer.Ordinal);
            // Containment by DN, also for objects that are only in state (removed from the configuration).
            var nodes = resources.Values.Where(r => !string.IsNullOrEmpty(r.Dn))
                .Select(r => (r.Address, r.Dn, r.Connection, Attachment: r.Type is AttachmentType))
                .Concat(_state.Resources.Where(s => !resources.ContainsKey(s.Address))
                    .Select(s => (s.Address, s.Dn, s.Connection, Attachment: ResourceRegistry.Find(s.Type) is AttachmentType)))
                .ToList();
            foreach (var node in nodes)
            {
                edges.TryAdd(node.Address, new HashSet<string>(StringComparer.Ordinal));
                foreach (var parent in nodes.Where(o => !o.Attachment && o.Address != node.Address && o.Connection == node.Connection
                                                         && (Dn.IsDescendantOf(node.Dn, o.Dn) || (node.Attachment && Dn.AreEqual(node.Dn, o.Dn)))))
                {
                    edges[node.Address].Add(parent.Address);
                }
            }
            if (_options.Destroy)
            {
                // Reverse the edges: destroying something also destroys what depends on it.
                var reversed = edges.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (var (from, deps) in edges)
                {
                    foreach (var dep in deps.Where(reversed.ContainsKey))
                    {
                        reversed[dep].Add(from);
                    }
                }
                edges = reversed;
            }

            var result = new HashSet<string>(_state.Resources.Select(s => s.Address).Concat(resources.Keys).Where(a => MatchesTarget(a, _options.Targets)), StringComparer.Ordinal);
            var queue = new Queue<string>(result);
            while (queue.Count > 0)
            {
                foreach (var next in edges.GetValueOrDefault(queue.Dequeue()) ?? new HashSet<string>())
                {
                    if (result.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
            return result;
        }

        public Plan Build()
        {
            var plan = new Plan();
            if (_config.Resources.FirstOrDefault(r => r.HasUnknowns) is { } unknown)
            {
                throw new DeclaradException($"{unknown.Address} has values that could not be determined; data sources need a directory connection.");
            }
            var targeted = TargetClosure();
            bool Targeted(string address) => targeted == null || targeted.Contains(address);
            var desired = _options.Destroy ? new List<ResourceInstance>() : _config.Resources.ToList();
            var desiredAddresses = desired.Select(r => r.Address).ToHashSet(StringComparer.Ordinal);

            ResolveMemberReferences(desired);
            foreach (var r in desired.Where(r => r.Type is AttachmentType))
            {
                r.Dn = r.Type.ComputeDn(r.Get, Schema(r.Connection));
            }
            var authoritativeGroups = MergeGroupsIntoAuthoritativeGroups(desired);

            var moved = ApplyMoves(desiredAddresses);
            var orphans = _state.Resources.Where(s => !desiredAddresses.Contains(s.Address) && !moved.Values.Contains(s)).ToList();
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            var upserts = new List<ResourceChange>();
            foreach (var r in desired.Where(r => Targeted(r.Address)))
            {
                var change = r.Type is AttachmentType ? PlanAttachment(r, moved, orphans, claimed, desired) : PlanResource(r, moved, orphans, claimed, authoritativeGroups);
                if (change != null)
                {
                    upserts.Add(change);
                }
            }

            var deletes = new List<ResourceChange>();
            foreach (var s in orphans.Where(o => !claimed.Contains(o.Address) && Targeted(o.Address)))
            {
                var change = PlanDelete(s);
                if (change != null)
                {
                    deletes.Add(change);
                }
            }

            if (_errors.Count > 0)
            {
                throw new ConfigErrorsException(_errors.ToList());
            }
            WarnAboutReplacedLabels(upserts, deletes, plan);

            MarkEarlyDeletes(upserts, deletes);
            plan.Changes.AddRange(deletes.Where(d => d.Early));
            deletes.RemoveAll(d => d.Early);
            plan.Changes.AddRange(OrderUpserts(upserts));
            plan.Changes.AddRange(deletes
                .OrderBy(c => c.IsAttachment ? 0 : 1)
                .ThenByDescending(c => c.Action == ChangeAction.Delete && !c.IsAttachment ? Dn.Depth(c.CurrentDn!) : 0)
                .ThenBy(c => c.Address, StringComparer.Ordinal));
            foreach (var w in _config.Warnings)
            {
                plan.Warnings.Add(w);
            }
            return plan;
        }

        /// <summary>
        /// A delete must run before the upserts if it frees a DN or a logon name that a created, renamed or moved
        /// object needs (e.g. one object renamed onto the name of another that is removed).
        /// </summary>
        private static void MarkEarlyDeletes(List<ResourceChange> upserts, List<ResourceChange> deletes)
        {
            foreach (var delete in deletes.Where(d => d.Action == ChangeAction.Delete && !d.IsAttachment))
            {
                string? sam = V.Str(delete.Prior?.Get("sam_account_name") ?? Value.Null);
                string? upn = V.Str(delete.Prior?.Get("user_principal_name") ?? Value.Null);
                delete.Early = upserts.Any(u => !u.IsAttachment && u.Action is ChangeAction.Create or ChangeAction.Update or ChangeAction.Restore && u.Connection == delete.Connection
                    && (Dn.AreEqual(u.TargetDn, delete.CurrentDn)
                        || (sam != null && string.Equals(V.Str(u.Desired!.Get("sam_account_name")), sam, StringComparison.OrdinalIgnoreCase))
                        || (upn != null && string.Equals(V.Str(u.Desired!.Get("user_principal_name")), upn, StringComparison.OrdinalIgnoreCase))));
            }
        }

        // ---- moved blocks ----------------------------------------------------------------------------

        /// <summary>Maps new addresses to the state entries that moved blocks transfer to them.</summary>
        private Dictionary<string, StateResource> ApplyMoves(HashSet<string> desiredAddresses)
        {
            var result = new Dictionary<string, StateResource>(StringComparer.Ordinal);
            foreach (var move in _config.Moves)
            {
                foreach (var s in _state.Resources)
                {
                    bool matches = s.Address == move.From
                        || s.Address.StartsWith(move.From + "[", StringComparison.Ordinal)
                        || s.Address.StartsWith(move.From + ".", StringComparison.Ordinal);
                    if (!matches)
                    {
                        continue;
                    }
                    string target = move.To + s.Address[move.From.Length..];
                    if (_state.Find(target) != null)
                    {
                        // Already moved by an earlier apply; the moved block is now a no-op.
                        continue;
                    }
                    if (!result.TryAdd(target, s))
                    {
                        _errors.Add(new DeclaradException($"Several moved blocks move state to {target}.", move.Pos));
                    }
                }
            }
            return result;
        }

        // ---- Membership references -----------------------------------------------------------------

        private void ResolveMemberReferences(List<ResourceInstance> desired)
        {
            foreach (var r in desired)
            {
                foreach (var spec in r.Type.Schema.Where(s => s.IsDn && s.AcceptsSamAccountName))
                {
                    var value = r.Get(spec.Name);
                    if (value.IsNull)
                    {
                        continue;
                    }
                    var pos = r.AttributePos.GetValueOrDefault(spec.Name, r.Pos);
                    if (spec.Type == AttrType.StringList)
                    {
                        var resolved = new List<string>();
                        foreach (string item in V.List(value))
                        {
                            if (ResolveDn(item, r, spec, pos) is { } dn && !resolved.Any(x => Dn.AreEqual(x, dn)))
                            {
                                resolved.Add(dn);
                            }
                        }
                        r.Attributes[spec.Name] = Value.FromStrings(resolved);
                    }
                    else if (ResolveDn(V.Str(value)!, r, spec, pos) is { } dn)
                    {
                        r.Attributes[spec.Name] = Value.From(dn);
                    }
                }
            }
        }

        private string? ResolveDn(string reference, ResourceInstance owner, AttributeSpec spec, SourcePos pos)
        {
            if (reference.Contains('='))
            {
                if (Dn.IsValid(reference))
                {
                    return reference;
                }
                _errors.Add(new DeclaradException($"{owner.Address}: '{reference}' in {spec.Name} is not a valid distinguished name.", pos));
                return null;
            }

            var managed = _config.Resources.FirstOrDefault(c =>
                c.Type is not AttachmentType && c.Connection == owner.Connection && string.Equals(V.Str(c.Get("sam_account_name")), reference, StringComparison.OrdinalIgnoreCase));
            if (managed != null)
            {
                return managed.Dn;
            }

            string cacheKey = owner.Connection + ":" + reference;
            if (!_samCache.TryGetValue(cacheKey, out var found))
            {
                var directory = Dir(owner.Connection);
                var matches = directory.Search(directory.BaseDn, $"(sAMAccountName={Dn.EscapeFilterValue(reference)})", subtree: true, Array.Empty<string>());
                found = matches.Count == 1 ? matches[0].Dn : null;
                _samCache[cacheKey] = found;
            }
            if (found == null)
            {
                _errors.Add(new DeclaradException($"{owner.Address}: {spec.Name} refers to '{reference}', which is neither a DN nor a sAMAccountName found in the directory or configuration.", pos));
            }
            return found;
        }

        /// <summary>
        /// An object listing (in <c>groups</c>) a group whose members are managed authoritatively is folded into that
        /// group's member list, so the two declarations never fight. Returns the normalized DNs of authoritative groups.
        /// </summary>
        private static HashSet<string> MergeGroupsIntoAuthoritativeGroups(List<ResourceInstance> desired)
        {
            var groups = desired
                .Where(r => r.Type is GroupResource && !r.Get("members").IsNull && !r.IgnoreChanges.Contains("members"))
                .ToDictionary(r => r.Connection + ":" + Dn.Normalize(r.Dn), StringComparer.Ordinal);

            foreach (var member in desired.Where(r => r.Type.Find("groups") is { Additive: true } && !r.Get("groups").IsNull))
            {
                var remaining = new List<string>();
                foreach (string groupDn in V.List(member.Get("groups")))
                {
                    if (groups.TryGetValue(member.Connection + ":" + Dn.Normalize(groupDn), out var group))
                    {
                        var members = V.List(group.Get("members")).ToList();
                        if (!members.Any(m => Dn.AreEqual(m, member.Dn)))
                        {
                            members.Add(member.Dn);
                            group.Attributes["members"] = Value.FromStrings(members);
                        }
                    }
                    else
                    {
                        remaining.Add(groupDn);
                    }
                }
                member.Attributes["groups"] = Value.FromStrings(remaining);
            }

            // A group_membership on a group whose members are managed authoritatively joins that member list.
            foreach (var membership in desired.Where(r => r.Type is GroupMembershipResource))
            {
                string groupDn = V.Str(membership.Get("group")) ?? "";
                if (groupDn.Contains('=') && groups.TryGetValue(membership.Connection + ":" + Dn.Normalize(groupDn), out var group))
                {
                    var members = V.List(group.Get("members")).ToList();
                    members.AddRange(V.List(membership.Get("members")).Where(m => !members.Any(x => Dn.AreEqual(x, m))));
                    group.Attributes["members"] = Value.FromStrings(members);
                }
            }
            return groups.Values.Select(g => Dn.Normalize(g.Dn)).ToHashSet(StringComparer.Ordinal);
        }

        // ---- Per-resource planning -----------------------------------------------------------------

        private StateResource? FindPrior(ResourceInstance r, Dictionary<string, StateResource> moved, out string? note)
        {
            note = null;
            var prior = _state.Find(r.Address);
            if (prior == null && moved.TryGetValue(r.Address, out var from))
            {
                prior = from;
                note = $"moved from {from.Address}";
            }
            return prior;
        }

        /// <summary>
        /// Finds the state entry of a resource whose block label was renamed: an entry of the same type and connection
        /// whose address is no longer in the configuration and that describes the same object. Like a moved block, the
        /// object keeps its state (and objectGUID) under the new address instead of being deleted and created again.
        /// </summary>
        private static StateResource? ClaimRenamed(ResourceInstance r, List<StateResource> orphans, HashSet<string> claimed, Func<StateResource, bool> sameObject)
        {
            var match = orphans.FirstOrDefault(o => o.Type == r.Type.Name && o.Connection == r.Connection && !claimed.Contains(o.Address) && sameObject(o));
            if (match != null)
            {
                claimed.Add(match.Address);
            }
            return match;
        }

        /// <summary>
        /// The same object: the same DN, the same name (moved to another container), or the same logon name for types
        /// that have one (renamed).
        /// </summary>
        private static bool SameObject(StateResource prior, ResourceInstance r)
        {
            if (Dn.AreEqual(prior.Dn, r.Dn) || string.Equals(Dn.First(prior.Dn).Value, V.Str(r.Get("name")), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return r.Type.Find("sam_account_name") != null
                && V.Str(r.Get("sam_account_name")) is { Length: > 0 } sam
                && string.Equals(V.Str(prior.Get("sam_account_name")), sam, StringComparison.OrdinalIgnoreCase);
        }

        private ResourceChange? PlanResource(ResourceInstance r, Dictionary<string, StateResource> moved, List<StateResource> orphans, HashSet<string> claimed, HashSet<string> authoritativeGroups)
        {
            var type = r.Type;
            var directory = Dir(r.Connection);
            var prior = FindPrior(r, moved, out string? notes);

            if (prior == null && ClaimRenamed(r, orphans, claimed, o => SameObject(o, r)) is { } renamed)
            {
                prior = renamed;
                notes = $"moved from {renamed.Address}";
            }

            DirectoryObject? actual = null;
            DirectoryObject? restoreFrom = null;
            bool adopt = false;
            if (prior != null)
            {
                if (prior.Connection != r.Connection)
                {
                    _errors.Add(new DeclaradException($"{r.Address} moved from connection '{prior.Connection}' to '{r.Connection}'; objects cannot move between directories. Remove it from state (declarad state rm) and import it in the new directory.", r.Pos));
                    return null;
                }
                actual = directory.GetByGuid(prior.Guid, type.LdapAttributes, type.NeedsSecurityDescriptor);
                if (actual == null && _options.Restore && directory.GetDeletedByGuid(prior.Guid, type.LdapAttributes) is { } deleted && type.Matches(deleted))
                {
                    restoreFrom = deleted;
                    notes = "deleted outside declarad; it is in the AD Recycle Bin and will be restored with its SID, attributes and group memberships";
                }
                else if (actual == null)
                {
                    notes = "the object was deleted outside declarad and will be created again, as a new object with a new SID";
                }
            }
            if (actual == null && prior == null && _config.Imports.FirstOrDefault(i => i.To == r.Address) is { } import)
            {
                actual = Guid.TryParse(import.Id, out var guid)
                    ? directory.GetByGuid(guid, type.LdapAttributes, type.NeedsSecurityDescriptor)
                    : directory.GetByDn(import.Id, type.LdapAttributes, type.NeedsSecurityDescriptor);
                if (actual == null)
                {
                    _errors.Add(new DeclaradException($"import block for {r.Address}: object '{import.Id}' was not found.", import.Pos));
                    return null;
                }
                if (_state.Resources.FirstOrDefault(s => s.Guid == actual.Guid && s.Connection == r.Connection) is { } owner)
                {
                    _errors.Add(new DeclaradException($"import block for {r.Address}: '{actual.Dn}' is already managed as {owner.Address}.", import.Pos));
                    return null;
                }
                adopt = true;
                notes = $"imported from {import.Id}";
            }
            if (restoreFrom != null && directory.GetByDn(r.Dn, Array.Empty<string>()) is { } occupant)
            {
                _errors.Add(new DeclaradException(
                    $"{r.Address}: the deleted object can be restored from the AD Recycle Bin, but '{occupant.Dn}' is now taken by another object. " +
                    "Rename or remove that object, or run with --no-restore to create a new one.", r.Pos));
                return null;
            }
            if (actual == null && restoreFrom == null)
            {
                var existing = directory.GetByDn(r.Dn, type.LdapAttributes, type.NeedsSecurityDescriptor);
                var owner = existing == null ? null : _state.Resources.FirstOrDefault(s => s.Guid == existing.Guid && s.Connection == r.Connection);
                if (existing != null && owner != null && ClaimRenamed(r, orphans, claimed, o => o == owner) != null)
                {
                    // The object at this DN belongs to a resource that left the configuration (its recorded DN may be
                    // out of date, e.g. after a rename outside declarad): the block label was renamed.
                    prior = owner;
                    actual = existing;
                    notes = $"moved from {owner.Address}";
                }
                else if (existing != null && owner != null)
                {
                    _errors.Add(new DeclaradException(
                        $"{r.Address}: '{existing.Dn}' is already managed as {owner.Address}, which is also in the configuration. " +
                        "Two resources cannot manage one object; give one of them a different name or path.", r.Pos));
                    return null;
                }
                else if (existing != null)
                {
                    if (!_options.Adopt)
                    {
                        _errors.Add(new DeclaradException(
                            $"{r.Address}: '{existing.Dn}' already exists in the directory but is not managed by declarad. " +
                            $"Import it with: declarad import '{r.Address}' '{existing.Dn}', an import block, or --adopt.", r.Pos));
                        return null;
                    }
                    actual = existing;
                    adopt = true;
                    notes = "existing object will be taken under management";
                }
            }

            if (actual != null && !type.Matches(actual))
            {
                _errors.Add(new DeclaradException($"{r.Address}: '{actual.Dn}' is a {actual.StructuralClass}, not a {type.ObjectClass}.", r.Pos));
                return null;
            }

            var change = new ResourceChange
            {
                Address = r.Address,
                Type = type,
                Desired = r,
                Actual = actual,
                Prior = prior,
                Adopt = adopt || (prior != null && prior.Address != r.Address),
            };
            if (notes != null)
            {
                change.Notes.Add(notes);
            }
            change.DependsOn.UnionWith(r.DependsOn);

            if (restoreFrom != null)
            {
                // The restore puts the object at the configured DN; other differences are applied right after it.
                change.Actual = restoreFrom;
                change.Action = ChangeAction.Restore;
                Diff(change, type.Read(restoreFrom, directory), authoritativeGroups);
                change.Changes.RemoveAll(c => c.Name is "name" or "path");
                DropLinksTheRestoreBringsBack(change, prior!);
            }
            else if (actual == null)
            {
                change.Action = ChangeAction.Create;
                AddAllAsCreated(change, r);
                foreach (var message in type.ValidateCreate(r, _options.Encrypted))
                {
                    _errors.Add(new DeclaradException($"{r.Address}: {message}", r.Pos));
                }
            }
            else
            {
                Diff(change, type.Read(actual, directory), authoritativeGroups);
                ResolveRemovalGuids(change, directory);
                foreach (var c in change.Changes.Where(c => c.Spec.Immutable))
                {
                    _errors.Add(new DeclaradException($"{r.Address}: {c.Name} cannot be changed after creation ({c.Old} -> {c.New}). Create a new resource instead.", r.AttributePos.GetValueOrDefault(c.Name, r.Pos)));
                }
                change.Action = change.Changes.Count > 0 ? ChangeAction.Update : ChangeAction.NoOp;
            }
            return change;
        }

        /// <summary>
        /// A renamed block keeps its object unless its name and logon name both changed; then it describes a different
        /// object, which plans a delete and a create. Say how to keep the object.
        /// </summary>
        private static void WarnAboutReplacedLabels(List<ResourceChange> upserts, List<ResourceChange> deletes, Plan plan)
        {
            foreach (var delete in deletes.Where(d => !d.IsAttachment && d.Action == ChangeAction.Delete))
            {
                var created = upserts.FirstOrDefault(c => c.Action == ChangeAction.Create && c.Prior == null && !c.IsAttachment
                    && c.Type.Name == delete.Type.Name && c.Desired!.Connection == delete.Prior!.Connection);
                if (created != null)
                {
                    plan.Warnings.Add($"{delete.Address} will be deleted and {created.Address} created as a new object (with a new SID), " +
                        "because its name and logon name both differ. If they are the same object (a renamed block whose name changed too), " +
                        $"keep it with a moved block with from = {delete.Address} and to = {created.Address}, or rename the block and the object in separate applies.");
                }
            }
        }

        /// <summary>
        /// A deleted object reads without its links (memberships, manager), but the restore brings them back. Values
        /// the object had when declarad last applied it are therefore not changes.
        /// </summary>
        private static void DropLinksTheRestoreBringsBack(ResourceChange change, StateResource prior)
        {
            for (int i = change.Changes.Count - 1; i >= 0; i--)
            {
                var c = change.Changes[i];
                if (!c.Spec.Deferred)
                {
                    continue;
                }
                if (c.Spec.Type == AttrType.StringList)
                {
                    Func<string, string> key = c.Spec.IsDn ? Dn.Normalize : x => x;
                    var had = V.List(prior.Get(c.Name)).Select(key).ToHashSet(StringComparer.Ordinal);
                    var added = c.Added.Where(a => !had.Contains(key(a))).ToList();
                    if (added.Count == 0 && c.Removed.Count == 0)
                    {
                        change.Changes.RemoveAt(i);
                    }
                    else
                    {
                        change.Changes[i] = c with { Added = added };
                    }
                }
                else if (c.New.Equals(prior.Get(c.Name)))
                {
                    change.Changes.RemoveAt(i);
                }
            }
        }

        private static void AddAllAsCreated(ResourceChange change, ResourceInstance r)
        {
            foreach (var spec in r.Type.Schema)
            {
                var v = r.Get(spec.Name);
                if (V.IsEmpty(v) || (v is ListValue l && l.Items.Length == 0))
                {
                    continue;
                }
                change.Changes.Add(new AttributeChange(spec, Value.Null, v) { Added = spec.Type == AttrType.StringList ? V.List(v) : Array.Empty<string>() });
            }
        }

        private ResourceChange? PlanAttachment(ResourceInstance r, Dictionary<string, StateResource> moved, List<StateResource> orphans, HashSet<string> claimed, List<ResourceInstance> desired)
        {
            var type = (AttachmentType)r.Type;
            var directory = Dir(r.Connection);
            var prior = FindPrior(r, moved, out string? note);
            if (prior == null && ClaimRenamed(r, orphans, claimed, o => (Dn.AreEqual(o.Dn, r.Dn) || string.Equals(V.Str(o.Get(type.TargetAttribute)), V.Str(r.Get(type.TargetAttribute)), StringComparison.OrdinalIgnoreCase))
                    && type.IdentityAttributes
                    .Where(a => a != type.TargetAttribute)
                    .All(a => string.Equals(V.Str(o.Get(a)), V.Str(r.Get(a)), StringComparison.OrdinalIgnoreCase))) is { } renamed)
            {
                // Same target and identity as an attachment that left the configuration: the block label was renamed.
                // Without this the new attachment would attach and the old one would detach the same items.
                prior = renamed;
                note = $"moved from {renamed.Address}";
            }
            var previousTarget = prior != null ? directory.GetByGuid(prior.Guid, type.TargetLdapAttributes) : null;
            var target = directory.GetByDn(r.Dn, type.TargetLdapAttributes);
            bool createdInThisPlan = type.CreatesTarget || desired.Any(d => d.Type is not AttachmentType && d.Connection == r.Connection && Dn.AreEqual(d.Dn, r.Dn));
            if (target == null && !createdInThisPlan)
            {
                _errors.Add(new DeclaradException($"{r.Address}: target '{r.Dn}' does not exist and is not declared in the configuration.", r.Pos));
                return null;
            }

            // With no state yet but an existing target, compare against what is there: the attachment may already be in effect.
            bool adopting = prior == null && target != null;
            var change = new ResourceChange { Address = r.Address, Type = type, Desired = r, Actual = target, Prior = prior, Adopt = adopting || (prior != null && prior.Address != r.Address) };
            if (note != null)
            {
                change.Notes.Add(note);
            }

            bool sameTarget = previousTarget != null && target != null && previousTarget.Guid == target.Guid;
            bool identityChanged = prior != null && (!sameTarget || type.IdentityAttributes
                .Where(a => a != type.TargetAttribute)
                .Any(a => !Equals(V.Str(prior.Get(a)), V.Str(r.Get(a)))));

            if (prior == null && target == null)
            {
                change.Action = ChangeAction.Create;
                AddAllAsCreated(change, r);
            }
            else if (adopting)
            {
                Diff(change, type.ReadAttachment(target!, r.Get, directory), new HashSet<string>());
                change.Action = change.Changes.Count > 0 ? ChangeAction.Create : ChangeAction.NoOp;
            }
            else if (identityChanged)
            {
                change.Action = previousTarget == null ? ChangeAction.Create : ChangeAction.Replace;
                if (change.Action == ChangeAction.Replace && prior!.PreventDestroy)
                {
                    _errors.Add(new DeclaradException($"{r.Address} has lifecycle.prevent_destroy = true, and this change would detach it from its current target. Remove that setting and apply first.", r.Pos));
                    return null;
                }
                AddAllAsCreated(change, r);
            }
            else
            {
                Diff(change, type.ReadAttachment(target!, r.Get, directory), new HashSet<string>());
                ResolveRemovalGuids(change, directory);
                change.Action = change.Changes.Count > 0 ? ChangeAction.Update : ChangeAction.NoOp;
            }
            return change;
        }

        private static void Diff(ResourceChange change, Dictionary<string, Value> actual, HashSet<string> authoritativeGroups)
        {
            var r = change.Desired!;
            foreach (var spec in r.Type.Schema)
            {
                if (spec.CreateOnly || r.IgnoreChanges.Contains(spec.Name))
                {
                    continue;
                }
                var desired = r.Get(spec.Name);
                var prior = change.Prior?.Get(spec.Name) ?? Value.Null;
                var current = actual.GetValueOrDefault(spec.Name, Value.Null);

                if (spec.Type == AttrType.StringList)
                {
                    DiffList(change, spec, desired, prior, current, authoritativeGroups);
                    continue;
                }

                // An attribute is managed while it is set in the configuration, and once more after it is
                // removed (to clear the value declarad wrote).
                if (desired.IsNull && prior.IsNull)
                {
                    continue;
                }
                bool equal = V.IsEmpty(desired) && V.IsEmpty(current)
                    || (spec.IsDn ? Dn.AreEqual(V.Str(desired), V.Str(current)) : desired.Equals(current));
                if (!equal)
                {
                    change.Changes.Add(new AttributeChange(spec, current, desired));
                }
            }
        }

        private static void DiffList(ResourceChange change, AttributeSpec spec, Value desired, Value prior, Value current, HashSet<string> authoritativeGroups)
        {
            Func<string, string> key = spec.IsDn ? Dn.Normalize : s => s;
            var desiredItems = V.List(desired);
            var currentItems = V.List(current);
            var currentKeys = currentItems.Select(key).ToHashSet(StringComparer.Ordinal);
            var desiredKeys = desiredItems.Select(key).ToHashSet(StringComparer.Ordinal);

            List<string> added;
            List<string> removed;
            if (spec.Additive)
            {
                if (desired.IsNull && prior.IsNull)
                {
                    return;
                }
                added = desiredItems.Where(d => !currentKeys.Contains(key(d))).ToList();
                // Only remove what declarad added earlier and is no longer listed.
                var priorKeys = V.List(prior).Select(key).ToHashSet(StringComparer.Ordinal);
                removed = currentItems
                    .Where(c => priorKeys.Contains(key(c)) && !desiredKeys.Contains(key(c)) && !authoritativeGroups.Contains(key(c)))
                    .ToList();
            }
            else
            {
                if (desired.IsNull)
                {
                    return; // Not managed.
                }
                added = desiredItems.Where(d => !currentKeys.Contains(key(d))).ToList();
                removed = currentItems.Where(c => !desiredKeys.Contains(key(c))).ToList();
            }

            if (added.Count > 0 || removed.Count > 0)
            {
                change.Changes.Add(new AttributeChange(spec, current, desired) { Added = added, Removed = removed });
            }
        }

        /// <summary>
        /// Remembers the objectGUID of every DN planned for removal from a list. If phase 1 renames that object, AD
        /// rewrites the member value and the applier finds the new DN through the GUID.
        /// </summary>
        private static void ResolveRemovalGuids(ResourceChange change, IDirectory directory)
        {
            for (int i = 0; i < change.Changes.Count; i++)
            {
                var c = change.Changes[i];
                if (c.Spec.IsDn && c.Removed.Count > 0)
                {
                    var guids = c.Removed.Select(dn => directory.GetByDn(dn, Array.Empty<string>())?.Guid).OfType<Guid>().ToList();
                    change.Changes[i] = c with { RemovedGuids = guids };
                }
            }
        }

        private ResourceChange? PlanDelete(StateResource s)
        {
            var type = ResourceRegistry.Find(s.Type);
            if (type == null)
            {
                _errors.Add(new DeclaradException($"State entry {s.Address} has unknown type '{s.Type}'."));
                return null;
            }
            var attributes = type is AttachmentType at ? at.TargetLdapAttributes : type.LdapAttributes;
            var actual = Dir(s.Connection).GetByGuid(s.Guid, attributes, type.NeedsSecurityDescriptor);
            var change = new ResourceChange { Address = s.Address, Type = type, Prior = s, Actual = actual };
            if (actual == null)
            {
                change.Action = ChangeAction.Forget;
                change.Notes.Add(type is AttachmentType ? "its target no longer exists; it will be removed from state" : "already deleted outside declarad; it will be removed from state");
                return change;
            }
            if (s.PreventDestroy)
            {
                _errors.Add(new DeclaradException($"{s.Address} has lifecycle.prevent_destroy = true and cannot be deleted. Remove that setting and apply first."));
                return null;
            }
            if (type.CannotDeleteReason(actual) is { } reason)
            {
                _errors.Add(new DeclaradException($"{s.Address} ('{actual.Dn}') cannot be deleted: {reason}"));
                return null;
            }
            change.Action = ChangeAction.Delete;
            return change;
        }

        // ---- Ordering ------------------------------------------------------------------------------

        /// <summary>
        /// Topological order over reference dependencies plus containment (a parent OU before anything
        /// placed in it). Ties are broken by type, then DN depth, then address.
        /// </summary>
        private static List<ResourceChange> OrderUpserts(List<ResourceChange> changes)
        {
            var byAddress = changes.ToDictionary(c => c.Address, StringComparer.Ordinal);
            var edges = changes.ToDictionary(c => c.Address, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            var objects = changes.Where(c => !c.IsAttachment).ToList();
            foreach (var c in changes)
            {
                foreach (var dep in c.DependsOn.Where(byAddress.ContainsKey))
                {
                    edges[c.Address].Add(dep);
                }
                if (c.IsAttachment)
                {
                    continue;
                }
                foreach (var other in objects.Where(o => o != c && o.Connection == c.Connection && Dn.IsDescendantOf(c.TargetDn!, o.TargetDn!)))
                {
                    edges[c.Address].Add(other.Address);
                    c.DependsOn.Add(other.Address);
                }
            }

            var rank = ResourceRegistry.All.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
            var comparer = Comparer<ResourceChange>.Create((a, b) =>
            {
                int cmp = rank[a.Type].CompareTo(rank[b.Type]);
                if (cmp == 0 && !a.IsAttachment && !b.IsAttachment)
                {
                    cmp = Dn.Depth(a.TargetDn!).CompareTo(Dn.Depth(b.TargetDn!));
                }
                if (cmp == 0 && a.Type is GpoLinkResource)
                {
                    // Links on one target are inserted from link order 1 upwards so each lands in its final position.
                    cmp = (V.Long(a.Desired!.Get("order")) ?? 0).CompareTo(V.Long(b.Desired!.Get("order")) ?? 0);
                }
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Address, b.Address);
            });

            var remaining = edges.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
            var dependents = changes.ToDictionary(c => c.Address, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var (from, deps) in edges)
            {
                foreach (var dep in deps)
                {
                    dependents[dep].Add(from);
                }
            }

            var ready = new SortedSet<ResourceChange>(changes.Where(c => remaining[c.Address] == 0), comparer);
            var ordered = new List<ResourceChange>();
            while (ready.Count > 0)
            {
                var next = ready.Min!;
                ready.Remove(next);
                ordered.Add(next);
                foreach (var dependent in dependents[next.Address])
                {
                    if (--remaining[dependent] == 0)
                    {
                        ready.Add(byAddress[dependent]);
                    }
                }
            }

            if (ordered.Count != changes.Count)
            {
                var cycle = changes.Where(c => remaining[c.Address] > 0).Select(c => c.Address);
                throw new DeclaradException($"Dependency cycle between: {string.Join(", ", cycle)}. Move one of the references into a deferred attribute (members, groups, manager) or remove depends_on.");
            }
            return ordered;
        }
    }
}
