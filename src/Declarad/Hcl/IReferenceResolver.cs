using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;

namespace Declarad.Hcl
{
    /// <summary>
    /// Resolves the root of a reference (e.g. <c>var</c>, <c>ou</c>). Returns the resolved value and how many
    /// traversal steps it consumed; the evaluator applies the remaining steps generically.
    /// </summary>
    public interface IReferenceResolver
    {
        (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate);
    }
}
