using Declarad.Hcl.Values;

namespace Declarad.Resources.Schema
{
    /// <summary>Declares one configurable attribute of a resource type and how it maps to the directory.</summary>
    public sealed record AttributeSpec(string Name, AttrType Type)
    {
        public string Description { get; init; } = "";
        public bool Required { get; init; }
        public Value? Default { get; init; }

        /// <summary>Default computed from other attributes (looked up through the function argument).</summary>
        public Func<Func<string, Value>, SchemaContext, Value>? DefaultFunc { get; init; }

        /// <summary>LDAP attribute for a plain single- or multi-valued string mapping; null when the type handles it.</summary>
        public string? Ldap { get; init; }

        public LdapFormat Format { get; init; } = LdapFormat.Text;

        /// <summary>Canonicalizes a configured value (e.g. "1008h" becomes "42d") so comparisons are exact.</summary>
        public Func<Value, Value>? Normalize { get; init; }

        /// <summary>Cannot change after creation (the plan fails instead of updating).</summary>
        public bool Immutable { get; init; }

        /// <summary>Never printed in plans or written to state.</summary>
        public bool Sensitive { get; init; }

        /// <summary>Only applied when the object is created (e.g. initial password); never compared afterwards.</summary>
        public bool CreateOnly { get; init; }

        /// <summary>
        /// Applied after all objects exist (memberships, manager links), so references between objects
        /// never force an ordering or create cycles.
        /// </summary>
        public bool Deferred { get; init; }

        /// <summary>Values are distinguished names; compared after normalization.</summary>
        public bool IsDn { get; init; }

        /// <summary>DN values may also be given as sAMAccountName and are resolved during planning.</summary>
        public bool AcceptsSamAccountName { get; init; }

        /// <summary>
        /// List attribute managed additively: entries are added when listed and removed only if declarad
        /// added them before (tracked in state). Other values are left alone.
        /// </summary>
        public bool Additive { get; init; }

        public IReadOnlyList<string>? AllowedValues { get; init; }

        /// <summary>Returns an error message for an invalid (non-null) value.</summary>
        public Func<Value, string?>? Validate { get; init; }

        public Value ResolveDefault(Func<string, Value> get, SchemaContext ctx) =>
            DefaultFunc != null ? DefaultFunc(get, ctx) : Default ?? Value.Null;
    }
}
