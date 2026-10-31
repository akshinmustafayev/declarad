using Declarad.Hcl.Values;

namespace Declarad.Config
{
    internal class Instance
    {
        public required Expandable Owner { get; init; }
        public Value? Key { get; init; }
        public Value EachKey { get; init; } = Value.Null;
        public Value EachValue { get; init; } = Value.Null;
    }
}
