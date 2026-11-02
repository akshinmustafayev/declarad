using System.Text;
using Declarad.Resources;
using Declarad.Resources.DataSources;
using Declarad.Resources.Schema;

namespace Declarad.Cli
{
    /// <summary>Renders resource and data source schemas as Markdown tables (used for docs/resources).</summary>
    public static class SchemaDocs
    {
        public static string Markdown(ResourceType type)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Attribute | Type | Default | Notes | Description |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var spec in type.Schema)
            {
                sb.AppendLine(Row(spec));
            }
            return sb.ToString();
        }

        public static string Markdown(DataSourceType data)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Argument | Type | Default | Notes | Description |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var spec in data.Arguments)
            {
                sb.AppendLine(Row(spec));
            }
            return sb.ToString();
        }

        private static string Row(AttributeSpec spec)
        {
            string type = spec.Type switch
            {
                AttrType.StringList => "list(string)",
                _ => spec.Type.ToString().ToLowerInvariant(),
            };
            string defaultText = spec.Default != null ? $"`{spec.Default.ToHcl()}`"
                : spec.DefaultFunc != null ? "computed"
                : "";
            var notes = new List<string>();
            if (spec.Required)
            {
                notes.Add("required");
            }
            if (spec.CreateOnly)
            {
                notes.Add("creation only");
            }
            if (spec.Immutable)
            {
                notes.Add("immutable");
            }
            if (spec.Sensitive)
            {
                notes.Add("sensitive");
            }
            if (spec.Additive)
            {
                notes.Add("additive");
            }
            else if (spec.Type == AttrType.StringList && spec.Ldap != null)
            {
                notes.Add("authoritative");
            }
            if (spec.AcceptsSamAccountName)
            {
                notes.Add("DN or sAMAccountName");
            }
            else if (spec.IsDn)
            {
                notes.Add("DN");
            }
            if (spec.AllowedValues != null)
            {
                notes.Add("one of " + string.Join(", ", spec.AllowedValues.Select(v => $"`{v}`")));
            }
            if (spec.Ldap != null)
            {
                notes.Add($"LDAP `{spec.Ldap}`");
            }
            return $"| `{spec.Name}` | {type} | {defaultText} | {string.Join("; ", notes)} | {spec.Description.Replace("|", "\\|")} |";
        }
    }
}
