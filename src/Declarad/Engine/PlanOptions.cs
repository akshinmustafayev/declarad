namespace Declarad.Engine
{
    public sealed record PlanOptions
    {
        /// <summary>Take existing objects that match a new resource's DN under management instead of failing.</summary>
        public bool Adopt { get; init; }

        /// <summary>Plan the deletion of everything in state.</summary>
        public bool Destroy { get; init; }

        /// <summary>Whether the connection is encrypted (passwords can only be set over LDAPS/StartTLS).</summary>
        public bool Encrypted { get; init; } = true;

        /// <summary>Limit the plan to these addresses (and resources inside these modules); empty means everything.</summary>
        public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
    }
}
