namespace Declarad.Config.Modules
{
    internal sealed class ModuleInst : Instance
    {
        public ModuleScope Child { get; set; } = null!;
    }
}
