using Declarad.Directory;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.DataSources
{
    /// <summary>Looks up a Group Policy Object by display name or id.</summary>
    public sealed class GpoDataSource : DataSourceType
    {
        public override string Name => "gpo";
        public override string Description => "Looks up an existing Group Policy Object.";

        public override IReadOnlyList<AttributeSpec> Arguments { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String) { Description = "Display name." },
            new AttributeSpec("id", AttrType.String) { Description = "GPO GUID." },
        };

        public override Value Read(IDirectory directory, Func<string, Value> args)
        {
            string? name = V.Str(args("name"));
            string? id = V.Str(args("id")) is { } raw ? GpoLinkResource.NormalizeGpo(raw) ?? throw new DeclaradException($"data.gpo: '{raw}' is not a GUID.") : null;
            if ((name == null) == (id == null))
            {
                throw new DeclaradException("data.gpo: set exactly one of name or id.");
            }
            string filter = name != null
                ? $"(&(objectClass=groupPolicyContainer)(displayName={Dn.EscapeFilterValue(name)}))"
                : $"(&(objectClass=groupPolicyContainer)(cn={Dn.EscapeFilterValue(id!)}))";
            var found = directory.Search($"CN=Policies,CN=System,{directory.BaseDn}", filter, subtree: false, new[] { "displayName", "gPCFileSysPath", "flags", "versionNumber" });
            if (found.Count != 1)
            {
                throw new DeclaradException(found.Count == 0 ? $"data.gpo: no GPO found with {(name != null ? $"name \"{name}\"" : $"id {id}")}." : $"data.gpo: {found.Count} GPOs are named \"{name}\"; use id.");
            }
            var gpo = found[0];
            return Object(new Dictionary<string, Value>
            {
                ["id"] = Value.From(GpoLinkResource.NormalizeGpo(gpo.Dn)),
                ["dn"] = Value.From(gpo.Dn),
                ["name"] = Value.From(gpo.Get("displayName")),
                ["file_sys_path"] = Value.From(gpo.Get("gPCFileSysPath")),
                ["status"] = Value.From((gpo.GetLong("flags") ?? 0) switch
                {
                    1 => "user_settings_disabled",
                    2 => "computer_settings_disabled",
                    3 => "all_settings_disabled",
                    _ => "all_settings_enabled",
                }),
            });
        }
    }
}
