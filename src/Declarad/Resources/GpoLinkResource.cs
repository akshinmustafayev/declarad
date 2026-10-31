using System.Text.RegularExpressions;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>
    /// Links an existing Group Policy Object to an OU (or the domain root) through the target's gPLink attribute.
    /// Link order 1 is the highest precedence, as in the Group Policy Management Console.
    /// </summary>
    public sealed partial class GpoLinkResource : AttachmentType
    {
        private const int DisabledFlag = 1;
        private const int EnforcedFlag = 2;

        public override string Name => "gpo_link";
        public override string ObjectClass => "organizationalUnit";
        public override string Description => "Link of an existing GPO to an OU or the domain";
        public override string TargetAttribute => "target";
        public override IReadOnlyList<string> IdentityAttributes => new[] { "target", "gpo" };
        public override IReadOnlyCollection<string> TargetLdapAttributes => new[] { "gPLink" };

        [GeneratedRegex(@"\[LDAP://([^;\]]+);(\d+)\]", RegexOptions.IgnoreCase)]
        private static partial Regex LinkPattern();

        [GeneratedRegex(@"\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?")]
        private static partial Regex GuidPattern();

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("gpo", AttrType.String)
            {
                Description = "GPO id ({GUID}) or its DN, e.g. data.gpo.baseline.id.",
                Required = true,
                Deferred = true,
                Validate = v => NormalizeGpo(V.Str(v)!) == null ? $"'{V.Str(v)}' is not a GPO GUID or DN." : null,
                Normalize = v => v is StringValue s && NormalizeGpo(s.V) is { } id ? Value.From(id) : v,
            },
            new AttributeSpec("target", AttrType.String)
            {
                Description = "DN of the OU (or the domain root) to link the GPO to.",
                Required = true,
                IsDn = true,
                Deferred = true,
                Validate = CommonSpecs.ValidDn,
            },
            new AttributeSpec("enabled", AttrType.Bool)
            {
                Description = "Whether the link is enabled.",
                Default = Value.True,
                Deferred = true,
            },
            new AttributeSpec("enforced", AttrType.Bool)
            {
                Description = "Whether the link is enforced (No Override).",
                Default = Value.False,
                Deferred = true,
            },
            new AttributeSpec("order", AttrType.Number)
            {
                Description = "Link order on the target (1 = highest precedence). Unmanaged when omitted.",
                Deferred = true,
                Validate = v => V.Long(v) < 1 ? "must be at least 1." : null,
            },
        };

        /// <summary>Returns "{GUID}" in upper case for a GUID or a GPO DN, or null.</summary>
        public static string? NormalizeGpo(string text)
        {
            var m = GuidPattern().Match(text);
            return m.Success ? "{" + m.Groups[1].Value.ToUpperInvariant() + "}" : null;
        }

        private sealed record Link(string Path, int Flags)
        {
            public string? Gpo => NormalizeGpo(Path);
            public override string ToString() => $"[LDAP://{Path};{Flags}]";
        }

        /// <summary>The GPO links in a gPLink value, with link order 1 = highest precedence.</summary>
        public static IReadOnlyList<(string Gpo, bool Enabled, bool Enforced, int Order)> ReadLinks(string? gpLink)
        {
            var links = ParseLinks(gpLink);
            return links.Select((l, i) => (l.Gpo ?? "", (l.Flags & DisabledFlag) == 0, (l.Flags & EnforcedFlag) != 0, links.Count - i))
                .Where(l => l.Item1.Length > 0)
                .OrderBy(l => l.Item4)
                .ToList();
        }

        private static List<Link> ParseLinks(string? gpLink) =>
            LinkPattern().Matches(gpLink ?? "").Select(m => new Link(m.Groups[1].Value, int.Parse(m.Groups[2].Value))).ToList();

        public override Dictionary<string, Value> ReadAttachment(DirectoryObject target, Func<string, Value> identity, IDirectory directory)
        {
            string? gpo = V.Str(identity("gpo"));
            var links = ParseLinks(target.Get("gPLink"));
            int index = links.FindIndex(l => l.Gpo == gpo);
            var result = new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["gpo"] = Value.From(gpo),
                ["target"] = Value.From(target.Dn),
            };
            if (index < 0)
            {
                result["enabled"] = Value.Null;
                result["enforced"] = Value.Null;
                result["order"] = Value.Null;
                return result;
            }
            result["enabled"] = Value.From((links[index].Flags & DisabledFlag) == 0);
            result["enforced"] = Value.From((links[index].Flags & EnforcedFlag) != 0);
            // gPLink lists the lowest precedence first; link order 1 is the last entry.
            result["order"] = Value.From(links.Count - index);
            return result;
        }

        public override void Attach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            var desired = change.Desired!;
            string gpo = V.Str(desired.Get("gpo"))!;
            var links = ParseLinks(target.Get("gPLink"));
            int existing = links.FindIndex(l => l.Gpo == gpo);
            int flags = (V.Bool(desired.Get("enabled")) == false ? DisabledFlag : 0) | (V.Bool(desired.Get("enforced")) == true ? EnforcedFlag : 0);
            var link = new Link($"cn={gpo},cn=policies,cn=system,{ctx.Directory.BaseDn}", flags);

            int position = existing >= 0 ? existing : 0; // New links get the lowest precedence, like GPMC.
            if (existing >= 0)
            {
                links.RemoveAt(existing);
            }
            if (V.Long(desired.Get("order")) is { } order)
            {
                position = Math.Clamp(links.Count + 1 - (int)order, 0, links.Count);
            }
            links.Insert(Math.Min(position, links.Count), link);
            ctx.Directory.Modify(target.Dn, new[] { Modification.Replace("gPLink", string.Concat(links)) });
        }

        public override void Detach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            string? gpo = NormalizeGpo(V.Str(change.Prior?.Get("gpo") ?? Value.Null) ?? "");
            var links = ParseLinks(target.Get("gPLink"));
            if (links.RemoveAll(l => l.Gpo == gpo) == 0)
            {
                return;
            }
            ctx.Directory.Modify(target.Dn, new[] { links.Count == 0 ? Modification.Clear("gPLink") : Modification.Replace("gPLink", string.Concat(links)) });
        }
    }
}
