using Declarad.Hcl.Values;

namespace Declarad.Config
{
    public sealed record OutputValue(string Name, Value Value, bool Sensitive, string? Description);
}
