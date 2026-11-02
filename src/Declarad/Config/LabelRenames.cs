using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.Schema;
using Declarad.State;

namespace Declarad.Config
{
    /// <summary>
    /// Renaming a block label must not change the object it describes. The label is also the default for one
    /// attribute (a user's sam_account_name, the name of most other types), and other defaults follow from it (CN,
    /// UPN). So when a block is renamed, or moved with a moved block, its label-derived values keep coming from the
    /// old label; the state remembers that label for later runs.
    /// </summary>
    public static class LabelRenames
    {
        /// <summary>
        /// Returns the configuration to plan with: <paramref name="config"/>, or, when blocks were renamed, the
        /// configuration loaded again by <paramref name="reload"/> with the old labels.
        /// </summary>
        public static Configuration Resolve(Configuration config, StateFile state, Func<IReadOnlyDictionary<string, string>, Configuration> reload)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in Candidates(config))
            {
                if (state.Find(r.Address) is { LabelDefault: { } recorded })
                {
                    labels[r.Address] = recorded;
                }
            }
            foreach (var (address, from) in Moves(config, state))
            {
                if (EffectiveLabel(from) is { } label && label != Label(address, from.Type))
                {
                    labels[address] = label;
                }
            }
            var renames = DetectRenames(config, state, labels.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (address, from) in renames)
            {
                labels[address] = EffectiveLabel(from)!;
            }
            if (labels.Count == 0)
            {
                return config;
            }

            var reloaded = reload(labels);
            // A detected rename holds only if the renamed block, with the old label, describes the same object. If
            // other attributes that make up the DN changed too, it is a different object: plan it as new.
            var rejected = renames.Where(x => reloaded.Find(x.Key) is not { } r || !Dn.AreEqual(r.Dn, x.Value.Dn)).Select(x => x.Key).ToList();
            if (rejected.Count == 0)
            {
                return reloaded;
            }
            foreach (string address in rejected)
            {
                labels.Remove(address);
            }
            return labels.Count == 0 ? config : reload(labels);
        }

        /// <summary>The label of a state entry's address (null for for_each/count instances).</summary>
        public static string? Label(string address, string type)
        {
            int i = 0;
            // Skip module segments: module.<name> or module.<name>["key"] / [n].
            while (address.AsSpan(i).StartsWith("module."))
            {
                int next = address.IndexOf('.', i + "module.".Length);
                int bracket = address.IndexOf('[', i + "module.".Length);
                if (bracket >= 0 && (next < 0 || bracket < next))
                {
                    int close = address.IndexOf("].", bracket, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return null;
                    }
                    next = close + 1;
                }
                if (next < 0)
                {
                    return null;
                }
                i = next + 1;
            }
            string prefix = type + ".";
            if (!address.AsSpan(i).StartsWith(prefix))
            {
                return null;
            }
            string label = address[(i + prefix.Length)..];
            return label.Length == 0 || label.Contains('[') ? null : label;
        }

        /// <summary>The label a state entry's label-derived values come from.</summary>
        public static string? EffectiveLabel(StateResource s) => s.LabelDefault ?? Label(s.Address, s.Type);

        /// <summary>Single (not for_each/count) resources whose label is the default of one of their attributes.</summary>
        private static IEnumerable<ResourceInstance> Candidates(Configuration config) =>
            config.Resources.Where(r => r.Key == null && r.Type.LabelAttribute != null && r.Type is not AttachmentType);

        /// <summary>Configured addresses that a moved block gives the state of another address.</summary>
        private static IEnumerable<(string Address, StateResource From)> Moves(Configuration config, StateFile state)
        {
            var configured = Candidates(config).Select(r => r.Address).ToHashSet(StringComparer.Ordinal);
            foreach (var move in config.Moves)
            {
                foreach (var s in state.Resources)
                {
                    string? target = s.Address == move.From ? move.To
                        : s.Address.StartsWith(move.From + ".", StringComparison.Ordinal) || s.Address.StartsWith(move.From + "[", StringComparison.Ordinal)
                            ? move.To + s.Address[move.From.Length..]
                            : null;
                    if (target != null && configured.Contains(target) && state.Find(target) == null)
                    {
                        yield return (target, s);
                    }
                }
            }
        }

        /// <summary>
        /// Pairs blocks that are not in state with state entries whose address left the configuration: same type,
        /// connection and module, both single resources. With several renames in one module, pairs are chosen by
        /// how many attribute values they share.
        /// </summary>
        private static Dictionary<string, StateResource> DetectRenames(Configuration config, StateFile state, HashSet<string> handled)
        {
            var result = new Dictionary<string, StateResource>(StringComparer.Ordinal);
            var configured = config.Resources.Select(r => r.Address).ToHashSet(StringComparer.Ordinal);
            var movedFrom = config.Moves.Select(m => m.From).ToList();
            bool IsMoved(string address) => movedFrom.Any(f => address == f || address.StartsWith(f + ".", StringComparison.Ordinal) || address.StartsWith(f + "[", StringComparison.Ordinal));

            var added = Candidates(config)
                .Where(r => state.Find(r.Address) == null && !handled.Contains(r.Address) && !r.Get(r.Type.LabelAttribute!).IsUnknown)
                .ToList();
            var removed = state.Resources
                .Where(s => !configured.Contains(s.Address) && !IsMoved(s.Address) && EffectiveLabel(s) != null)
                .ToList();

            foreach (var group in added.GroupBy(r => (r.ModulePath, r.Type.Name, r.Connection)))
            {
                string prefix = (group.Key.ModulePath.Length > 0 ? group.Key.ModulePath + "." : "") + group.Key.Name + ".";
                var orphans = removed
                    .Where(s => s.Type == group.Key.Name && s.Connection == group.Key.Connection && s.Address.StartsWith(prefix, StringComparison.Ordinal) && !s.Address[prefix.Length..].Contains('['))
                    .ToList();
                var pairs = group
                    .SelectMany(r => orphans.Select(s => (r, s, Score: SharedValues(r, s))))
                    .OrderByDescending(p => p.Score)
                    .ToList();
                var used = new HashSet<StateResource>();
                foreach (var (r, s, _) in pairs)
                {
                    if (!result.ContainsKey(r.Address) && used.Add(s))
                    {
                        result[r.Address] = s;
                    }
                }
            }
            return result;
        }

        private static int SharedValues(ResourceInstance r, StateResource s) =>
            r.Type.Schema.Count(spec => !spec.Sensitive && !spec.CreateOnly && !V.IsEmpty(r.Get(spec.Name)) && r.Get(spec.Name).Equals(s.Get(spec.Name)));
    }
}
