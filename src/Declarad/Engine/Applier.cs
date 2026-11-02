using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.Schema;
using Declarad.State;
using Microsoft.Extensions.Logging;

namespace Declarad.Engine
{
    /// <summary>
    /// Executes a <see cref="Plan"/> in three phases:
    /// 1. create/rename/move/update objects in dependency order,
    /// 2. deferred attributes (memberships, manager links) and attachments, once every object exists,
    /// 3. detach removed attachments, then delete objects deepest DN first.
    /// State is saved after every step, so an interrupted apply leaves an accurate record. The first
    /// failure stops the run.
    /// </summary>
    public sealed class Applier
    {
        private readonly Func<string, IDirectory> _directories;
        private readonly StateFile _state;
        private readonly Action<StateFile> _saveState;
        private readonly ILogger _logger;
        private readonly Action<string, string> _progress;

        public Applier(IDirectory directory, StateFile state, Action<StateFile> saveState, ILogger logger, Action<string, string>? progress = null)
            : this(_ => directory, state, saveState, logger, progress)
        {
        }

        /// <param name="directories">The directory of each connection name.</param>
        public Applier(Func<string, IDirectory> directories, StateFile state, Action<StateFile> saveState, ILogger logger, Action<string, string>? progress = null)
        {
            _directories = directories;
            _state = state;
            _saveState = saveState;
            _logger = logger;
            _progress = progress ?? ((_, _) => { });
        }

        public ApplyResult Apply(Plan plan)
        {
            var result = new ApplyResult();
            var contexts = new Dictionary<string, ApplyContext>(StringComparer.Ordinal);
            ApplyContext Ctx(ResourceChange c)
            {
                if (!contexts.TryGetValue(c.Connection, out var ctx))
                {
                    contexts[c.Connection] = ctx = new ApplyContext { Directory = _directories(c.Connection), Logger = _logger };
                }
                return ctx;
            }

            // Phase 0: deletes that free a name a create or rename needs.
            foreach (var change in plan.Changes.Where(c => c.Early))
            {
                if (!Step(change, result, () => ApplyDelete(Ctx(change), change, result)))
                {
                    return result;
                }
            }

            var upserts = plan.Changes.Where(c => c.Action is ChangeAction.Create or ChangeAction.Update or ChangeAction.Restore or ChangeAction.NoOp or ChangeAction.Replace).ToList();
            foreach (var change in upserts.Where(c => !c.IsAttachment))
            {
                if (!Step(change, result, () => ApplyObject(Ctx(change), change, result)))
                {
                    return result;
                }
            }

            foreach (var change in upserts.Where(c => !c.IsAttachment && c.DeferredChanges.Any()))
            {
                if (!Step(change, result, () => ApplyDeferred(Ctx(change), change)))
                {
                    return result;
                }
            }

            foreach (var change in upserts.Where(c => c.IsAttachment))
            {
                if (!Step(change, result, () => ApplyAttachment(Ctx(change), change, result)))
                {
                    return result;
                }
            }

            foreach (var change in plan.Changes.Where(c => c.Action is ChangeAction.Delete or ChangeAction.Forget && !c.Early))
            {
                if (!Step(change, result, () => ApplyDelete(Ctx(change), change, result)))
                {
                    return result;
                }
            }
            return result;
        }

        private bool Step(ResourceChange change, ApplyResult result, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (DeclaradException ex)
            {
                result.Errors.Add($"{change.Address}: {ex.Message}");
                _progress(change.Address, "failed");
                return false;
            }
            finally
            {
                _saveState(_state);
            }
        }

        private void ApplyObject(ApplyContext ctx, ResourceChange change, ApplyResult result)
        {
            var type = change.Type;
            switch (change.Action)
            {
                case ChangeAction.Create:
                    _progress(change.Address, "creating...");
                    change.Actual = type.Create(ctx, change);
                    result.Created++;
                    _progress(change.Address, $"created ({change.Actual.Dn})");
                    break;

                case ChangeAction.Update:
                    Refresh(ctx, change);
                    if (change.ImmediateChanges.Any())
                    {
                        _progress(change.Address, "updating...");
                        type.Update(ctx, change);
                        Refresh(ctx, change);
                        _progress(change.Address, "updated");
                    }
                    result.Updated++;
                    break;

                case ChangeAction.Restore:
                    _progress(change.Address, "restoring from the AD Recycle Bin...");
                    ctx.Directory.Restore(change.Actual!.Dn, change.Desired!.Dn);
                    Refresh(ctx, change);
                    if (change.ImmediateChanges.Any())
                    {
                        type.Update(ctx, change);
                        Refresh(ctx, change);
                    }
                    result.Restored++;
                    _progress(change.Address, $"restored ({change.Actual.Dn})");
                    break;

                case ChangeAction.NoOp:
                    if (change.Adopt)
                    {
                        _progress(change.Address, change.IsAddressChange ? $"moved from {change.Prior!.Address}" : "taken under management");
                    }
                    break;
            }

            ForgetPreviousAddress(change);
            // Deferred values are recorded now only if nothing deferred is pending (then they already match).
            _state.Upsert(Record(change, includeDeferred: !change.DeferredChanges.Any()));
        }

        private void ForgetPreviousAddress(ResourceChange change)
        {
            if (change.Prior != null && change.Prior.Address != change.Address)
            {
                _state.Remove(change.Prior.Address);
            }
        }

        private void ApplyDeferred(ApplyContext ctx, ResourceChange change)
        {
            Refresh(ctx, change);
            RecomputeListDeltas(change, change.Type.Read(change.Actual!, ctx.Directory), ctx.Directory);
            if (change.DeferredChanges.Any())
            {
                _progress(change.Address, "updating " + string.Join(", ", change.DeferredChanges.Select(c => c.Name)) + "...");
                change.Type.ApplyDeferred(ctx, change);
                _progress(change.Address, "updated");
            }
            _state.Upsert(Record(change, includeDeferred: true));
        }

        private void ApplyAttachment(ApplyContext ctx, ResourceChange change, ApplyResult result)
        {
            var type = (AttachmentType)change.Type;
            var desired = change.Desired!;
            if (change.Action != ChangeAction.NoOp)
            {
                if (change.Action == ChangeAction.Replace && ctx.Directory.GetByGuid(change.Prior!.Guid, type.TargetLdapAttributes) is { } previous)
                {
                    _progress(change.Address, $"detaching from {previous.Dn}...");
                    type.Detach(ctx, change, previous);
                }
                var target = ctx.Directory.GetByDn(desired.Dn, type.TargetLdapAttributes);
                if (target == null)
                {
                    _progress(change.Address, $"creating {desired.Dn}...");
                    type.CreateTarget(ctx, change);
                }
                else
                {
                    RecomputeListDeltas(change, type.ReadAttachment(target, desired.Get, ctx.Directory), ctx.Directory);
                    _progress(change.Address, change.Action == ChangeAction.Update ? "updating..." : $"attaching to {target.Dn}...");
                    type.Attach(ctx, change, target);
                }
                if (change.Action == ChangeAction.Update)
                {
                    result.Updated++;
                }
                else
                {
                    result.Created++;
                }
                _progress(change.Address, "done");
            }
            change.Actual = ctx.Directory.GetByDn(desired.Dn, type.TargetLdapAttributes)
                ?? throw new DirectoryException($"Target '{desired.Dn}' disappeared during apply.");
            ForgetPreviousAddress(change);
            _state.Upsert(Record(change, includeDeferred: true));
        }

        /// <summary>
        /// List deltas are planned against values read before phase 1. Renames in phase 1 make AD rewrite
        /// member values, so the deltas are recomputed from the current values.
        /// </summary>
        private static void RecomputeListDeltas(ResourceChange change, Dictionary<string, Value> current, IDirectory directory)
        {
            for (int i = change.Changes.Count - 1; i >= 0; i--)
            {
                var c = change.Changes[i];
                if (!c.Spec.Deferred || c.Spec.Type != AttrType.StringList)
                {
                    continue;
                }
                Func<string, string> key = c.Spec.IsDn ? Dn.Normalize : s => s;
                var currentItems = V.List(current.GetValueOrDefault(c.Name, Value.Null));
                var currentKeys = currentItems.Select(key).ToHashSet(StringComparer.Ordinal);
                var desired = V.List(c.New);
                var desiredKeys = desired.Select(key).ToHashSet(StringComparer.Ordinal);
                // Planned removals by DN, plus where those objects are now if phase 1 renamed them.
                var plannedRemovals = c.Removed.Select(key)
                    .Concat(c.RemovedGuids.Select(g => directory.GetByGuid(g, Array.Empty<string>())?.Dn).OfType<string>().Select(key))
                    .ToHashSet(StringComparer.Ordinal);

                var added = desired.Where(d => !currentKeys.Contains(key(d))).ToList();
                // A planned removal that phase 1 renamed into a desired item (e.g. a group whose CN changed) stays.
                var removed = c.Spec.Additive
                    ? currentItems.Where(x => plannedRemovals.Contains(key(x)) && !desiredKeys.Contains(key(x))).ToList()
                    : currentItems.Where(x => !desiredKeys.Contains(key(x))).ToList();

                if (added.Count == 0 && removed.Count == 0)
                {
                    change.Changes.RemoveAt(i);
                }
                else
                {
                    change.Changes[i] = c with { Added = added, Removed = removed };
                }
            }
        }

        private void ApplyDelete(ApplyContext ctx, ResourceChange change, ApplyResult result)
        {
            if (change.Action == ChangeAction.Delete)
            {
                if (change.Type is AttachmentType attachment)
                {
                    var target = ctx.Directory.GetByGuid(change.Prior!.Guid, attachment.TargetLdapAttributes);
                    if (target != null)
                    {
                        _progress(change.Address, $"detaching from {target.Dn}...");
                        attachment.Detach(ctx, change, target);
                    }
                }
                else
                {
                    _progress(change.Address, "deleting...");
                    Refresh(ctx, change);
                    change.Type.Delete(ctx, change);
                }
                result.Deleted++;
                _progress(change.Address, "deleted");
            }
            _state.Remove(change.Address);
        }

        /// <summary>Re-reads the object by GUID; an earlier step may have moved it (e.g. by renaming its OU).</summary>
        private static void Refresh(ApplyContext ctx, ResourceChange change)
        {
            var guid = change.Actual?.Guid ?? change.Prior?.Guid ?? Guid.Empty;
            change.Actual = ctx.Directory.GetByGuid(guid, change.Type.LdapAttributes, change.Type.NeedsSecurityDescriptor)
                ?? throw new DirectoryException($"The object {guid} disappeared during apply.");
        }

        /// <summary>
        /// For additive lists, state records only what declarad added (now or earlier), so it never removes a
        /// membership somebody else created.
        /// </summary>
        private static Value OwnedItems(AttributeSpec spec, Value desired, Value prior, AttributeChange? change)
        {
            Func<string, string> key = spec.IsDn ? Dn.Normalize : s => s;
            var owned = V.List(prior).Concat(change?.Added ?? Array.Empty<string>()).Select(key).ToHashSet(StringComparer.Ordinal);
            return Value.FromStrings(V.List(desired).Where(d => owned.Contains(key(d))));
        }

        private StateResource Record(ResourceChange change, bool includeDeferred)
        {
            var desired = change.Desired!;
            var previous = _state.Find(change.Address) ?? change.Prior;
            var attributes = new Dictionary<string, Value>(StringComparer.Ordinal);
            foreach (var spec in change.Type.Schema.Where(s => !s.Sensitive && !s.CreateOnly))
            {
                var value = spec.Deferred && !includeDeferred ? previous?.Get(spec.Name) ?? Value.Null : desired.Get(spec.Name);
                if (spec.Additive && includeDeferred && !value.IsNull)
                {
                    value = OwnedItems(spec, value, previous?.Get(spec.Name) ?? Value.Null, change.Get(spec.Name));
                }
                if (!value.IsNull)
                {
                    attributes[spec.Name] = value;
                }
            }
            return new StateResource
            {
                Address = change.Address,
                Type = change.Type.Name,
                Connection = change.Connection,
                Guid = change.Actual!.Guid,
                Dn = change.Actual!.Dn,
                Attributes = attributes,
                PreventDestroy = desired.PreventDestroy,
                LabelDefault = desired.LabelDefault,
            };
        }
    }
}
