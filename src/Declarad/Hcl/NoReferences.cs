using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;

namespace Declarad.Hcl
{
    /// <summary>A resolver for contexts without references (template files, variable defaults).</summary>
    public sealed class NoReferences : IReferenceResolver
    {
        public static readonly NoReferences Instance = new();

        public (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate) =>
            throw new DeclaradException($"Unknown name '{reference.Root}'; no references are available here.", reference.Pos);
    }
}
