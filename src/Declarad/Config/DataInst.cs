using Declarad.Hcl.Values;

namespace Declarad.Config
{
    internal sealed class DataInst : Instance
    {
        public Value? Result { get; set; }
        public HashSet<string> Deps { get; set; } = new(StringComparer.Ordinal);
        public bool Evaluating { get; set; }
    }
}
