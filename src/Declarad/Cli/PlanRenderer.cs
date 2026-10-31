using System.Text;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Cli
{
    /// <summary>Renders a plan as a Terraform-style, color-coded diff.</summary>
    public sealed class PlanRenderer
    {
        private readonly ConsoleUi _ui;

        public PlanRenderer(ConsoleUi ui)
        {
            _ui = ui;
        }

        public string Render(Plan plan)
        {
            var sb = new StringBuilder();
            var visible = plan.Changes.Where(c => c.Action != ChangeAction.NoOp || c.Adopt).ToList();
            if (visible.Count == 0)
            {
                sb.AppendLine(_ui.Green(_ui.Bold("No changes.")) + " The directory matches the configuration.");
                return sb.ToString();
            }

            sb.AppendLine("declarad will perform the following actions:");
            foreach (var change in visible)
            {
                sb.AppendLine();
                RenderChange(sb, change);
            }
            sb.AppendLine();
            sb.AppendLine(Summary(plan));
            return sb.ToString();
        }

        public string Summary(Plan plan)
        {
            int adopt = plan.Changes.Count(c => c.Adopt);
            string text = $"Plan: {plan.ToAdd} to add, {plan.ToChange} to change, {plan.ToDestroy} to destroy";
            if (adopt > 0)
            {
                text += $", {adopt} to take under management";
            }
            return _ui.Bold(text + ".");
        }

        private void RenderChange(StringBuilder sb, ResourceChange change)
        {
            var (symbol, verb, color) = change.Action switch
            {
                ChangeAction.Create => ("+", "will be created", (Func<string, string>)_ui.Green),
                ChangeAction.Update => ("~", "will be updated in-place", _ui.Yellow),
                ChangeAction.Delete => ("-", "will be destroyed", _ui.Red),
                ChangeAction.Forget => ("-", "will be removed from state", _ui.Dim),
                ChangeAction.Replace => ("-/+", "must be replaced (attached to a new target)", _ui.Yellow),
                _ when change.Adopt => ("=", change.Prior != null && change.Prior.Address != change.Address ? "has moved" : "will be taken under management", _ui.Cyan),
                _ => ("=", "is unchanged", _ui.Cyan),
            };

            string dn = change.TargetDn ?? change.CurrentDn ?? change.Prior?.Dn ?? "";
            sb.AppendLine($"  {_ui.Bold($"# {change.Address}")} {verb}");
            if (change.Action == ChangeAction.Update && change.IsMove && change.CurrentDn != null && !Dn.AreEqual(change.CurrentDn, change.TargetDn))
            {
                sb.AppendLine(_ui.Dim($"  # moved: {change.CurrentDn} -> {change.TargetDn}"));
            }
            foreach (var note in change.Notes)
            {
                sb.AppendLine(_ui.Dim($"  # ({note})"));
            }
            sb.AppendLine($"  {color(symbol)} {change.Address} {{");

            if (change.Action is ChangeAction.Delete or ChangeAction.Forget)
            {
                sb.AppendLine($"      dn = {HclWriter.Quote(dn)}");
            }
            else
            {
                int width = change.Changes.Select(c => c.Name.Length).DefaultIfEmpty(0).Max();
                foreach (var c in change.Changes)
                {
                    RenderAttribute(sb, change.Action, c, width, change.Desired?.SensitiveAttributes.Contains(c.Name) == true);
                }
                if (change.Action != ChangeAction.Create || change.IsAttachment)
                {
                    sb.AppendLine(_ui.Dim($"      # {(change.IsAttachment ? "target" : "dn")} = {HclWriter.Quote(dn)}"));
                }
            }
            sb.AppendLine("    }");
        }

        public string RenderOutputs(IReadOnlyList<Config.OutputValue> outputs, string title)
        {
            if (outputs.Count == 0)
            {
                return "";
            }
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(_ui.Bold($"{title}:"));
            int width = outputs.Max(o => o.Name.Length);
            foreach (var o in outputs)
            {
                string value = o.Sensitive ? "(sensitive value)" : o.Value.ToHcl();
                sb.AppendLine($"  {o.Name.PadRight(width)} = {value}");
            }
            return sb.ToString();
        }

        private void RenderAttribute(StringBuilder sb, ChangeAction action, AttributeChange c, int width, bool sensitive)
        {
            string name = c.Name.PadRight(width);
            if (c.Spec.Type == AttrType.StringList && !sensitive)
            {
                string sym = action is ChangeAction.Create or ChangeAction.Replace ? _ui.Green("+") : _ui.Yellow("~");
                sb.AppendLine($"      {sym} {name} = [");
                foreach (var a in c.Added)
                {
                    sb.AppendLine($"          {_ui.Green("+")} {HclWriter.Quote(a)},");
                }
                foreach (var r in c.Removed)
                {
                    sb.AppendLine($"          {_ui.Red("-")} {HclWriter.Quote(r)},");
                }
                sb.AppendLine("        ]");
                return;
            }

            string Show(Value v) => c.Spec.Sensitive || sensitive ? "(sensitive value)" : v.IsNull ? "null" : v.ToHcl();
            if (action is ChangeAction.Create or ChangeAction.Replace)
            {
                sb.AppendLine($"      {_ui.Green("+")} {name} = {Show(c.New)}");
            }
            else if (c.New.IsNull || c.New is StringValue { V: "" })
            {
                sb.AppendLine($"      {_ui.Red("-")} {name} = {Show(c.Old)} -> null");
            }
            else if (c.Old.IsNull)
            {
                sb.AppendLine($"      {_ui.Green("+")} {name} = {Show(c.New)}");
            }
            else
            {
                sb.AppendLine($"      {_ui.Yellow("~")} {name} = {Show(c.Old)} -> {Show(c.New)}");
            }
        }
    }
}
