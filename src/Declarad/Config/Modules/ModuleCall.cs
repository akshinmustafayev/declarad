using Declarad.Hcl.Values;

namespace Declarad.Config.Modules
{
    internal sealed class ModuleCall : Expandable
    {
        public override string Describe => (Scope.Prefix.Length > 0 ? Scope.Prefix + "." : "") + $"module.{Label}";

        public override Instance NewInstance(Value? key, Value eachKey, Value eachValue)
        {
            var inst = new ModuleInst { Owner = this, Key = key, EachKey = eachKey, EachValue = eachValue };
            string prefix = (Scope.Prefix.Length > 0 ? Scope.Prefix + "." : "") + $"module.{Label}" + (key == null ? "" : ResourceInstance.FormatKey(key));
            inst.Child = Scope.CreateChild(this, inst, prefix);
            return inst;
        }
    }
}
