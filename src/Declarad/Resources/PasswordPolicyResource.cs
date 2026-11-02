using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>A fine-grained password policy (Password Settings Object).</summary>
    public sealed class PasswordPolicyResource : ResourceType
    {
        public override string Name => "password_policy";
        public override string ObjectClass => "msDS-PasswordSettings";
        public override string RdnType => "CN";
        public override string Description => "Fine-grained password policy";

        private static AttributeSpec Duration(string name, string ldap, string description, string defaultValue) => new(name, AttrType.String)
        {
            Ldap = ldap,
            Format = LdapFormat.Duration,
            Description = description,
            Default = Value.From(defaultValue),
            Validate = AdInterval.Validate,
            Normalize = AdInterval.Normalize,
        };

        /// <summary>The combinations AD itself rejects when the policy is written, reported up front instead.</summary>
        public override IEnumerable<string> Validate(Func<string, Value> get)
        {
            static TimeSpan? Duration(Value v) => AdInterval.TryParse(V.Str(v) ?? "0", out var span) ? span : TimeSpan.Zero;
            static bool IsNever(Value v) => string.Equals(V.Str(v), AdInterval.Never, StringComparison.OrdinalIgnoreCase);

            foreach (var (name, max) in new[] { ("min_password_length", 255L), ("password_history_count", 1024L), ("lockout_threshold", 65535L) })
            {
                if (V.Long(get(name)) is { } n && (n < 0 || n > max))
                {
                    yield return $"{name} must be between 0 and {max}.";
                }
            }
            if (IsNever(get("min_password_age")))
            {
                yield return "min_password_age cannot be \"never\".";
            }
            else if (!IsNever(get("max_password_age")) && Duration(get("min_password_age")) >= Duration(get("max_password_age")))
            {
                yield return "min_password_age must be shorter than max_password_age.";
            }
            if (IsNever(get("lockout_observation_window")))
            {
                yield return "lockout_observation_window cannot be \"never\".";
            }
            else if (!IsNever(get("lockout_duration")) && Duration(get("lockout_observation_window")) > Duration(get("lockout_duration")))
            {
                yield return "lockout_observation_window must not be longer than lockout_duration.";
            }
        }

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("Policy name (the CN)."),
            CommonSpecs.Path("CN=Password Settings Container,CN=System"),
            CommonSpecs.Str("description", "description", "Free-text description."),
            new AttributeSpec("precedence", AttrType.Number)
            {
                Ldap = "msDS-PasswordSettingsPrecedence",
                Format = LdapFormat.Int,
                Description = "Lower numbers win when several policies apply.",
                Required = true,
                Validate = v => V.Long(v) < 1 ? "must be at least 1." : null,
            },
            CommonSpecs.Number("min_password_length", "msDS-MinimumPasswordLength", "Minimum password length.", 7),
            CommonSpecs.Number("password_history_count", "msDS-PasswordHistoryLength", "Number of remembered passwords.", 24),
            new AttributeSpec("complexity_enabled", AttrType.Bool)
            {
                Ldap = "msDS-PasswordComplexityEnabled",
                Format = LdapFormat.Bool,
                Description = "Require complex passwords.",
                Default = Value.True,
            },
            new AttributeSpec("reversible_encryption_enabled", AttrType.Bool)
            {
                Ldap = "msDS-PasswordReversibleEncryptionEnabled",
                Format = LdapFormat.Bool,
                Description = "Store passwords with reversible encryption.",
                Default = Value.False,
            },
            Duration("min_password_age", "msDS-MinimumPasswordAge", "Minimum password age.", "1d"),
            Duration("max_password_age", "msDS-MaximumPasswordAge", "Maximum password age, or \"never\".", "42d"),
            CommonSpecs.Number("lockout_threshold", "msDS-LockoutThreshold", "Failed logons before lockout (0 = never lock).", 0),
            Duration("lockout_duration", "msDS-LockoutDuration", "Lockout duration, or \"never\" (until an admin unlocks).", "30m"),
            Duration("lockout_observation_window", "msDS-LockoutObservationWindow", "Window for counting failed logons.", "30m"),
            new AttributeSpec("applies_to", AttrType.StringList)
            {
                Ldap = "msDS-PSOAppliesTo",
                Description = "Users or global security groups (DN or sAMAccountName) the policy applies to. Authoritative.",
                IsDn = true,
                AcceptsSamAccountName = true,
                Deferred = true,
            },
        };
    }
}
