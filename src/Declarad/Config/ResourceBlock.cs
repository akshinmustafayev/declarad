using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;
using Declarad.Resources;

namespace Declarad.Config
{
    internal sealed class ResourceBlock : Expandable
    {
        public required ResourceType Type { get; init; }
        public override string Describe => ResourceInstance.FormatAddress(Scope.Prefix, Type.Name, Label, null);

        public override Instance NewInstance(Value? key, Value eachKey, Value eachValue) => new ResourceInst
        {
            Owner = this,
            Key = key,
            EachKey = eachKey,
            EachValue = eachValue,
            Result = new ResourceInstance { Type = Type, Name = Label, Key = key, ModulePath = Scope.Prefix, Pos = Block.Pos, Connection = Scope.ConnectionOf(Block) },
        };
    }
}
