# password_policy

A fine-grained password policy (Password Settings Object). It applies to the users and global security groups in `applies_to`, overriding the domain policy for them.

```hcl
group "tier0_admins" {}

password_policy "tier0" {
  precedence             = 10
  min_password_length    = 16
  password_history_count = 24
  max_password_age       = "60d"
  min_password_age       = "1d"
  lockout_threshold      = 5
  lockout_duration       = "never"        # until an admin unlocks
  lockout_observation_window = "30m"
  applies_to             = [group.tier0_admins.dn]
}
```

DN: `CN=<name>,CN=Password Settings Container,CN=System,<domain>`.

## Durations

`min_password_age`, `max_password_age`, `lockout_duration` and `lockout_observation_window` take durations: combinations of `d`, `h`, `m`, `s` (`"42d"`, `"1d12h"`, `"30m"`), `"0"`, or `"never"`. Values are normalized (`"1008h"` is the same as `"42d"`), so equivalent spellings don't cause changes.

## Behaviour

- Combinations AD would reject are errors at `validate` time:
  - `min_password_age` must be shorter than `max_password_age`, unless the maximum is `"never"`;
  - `lockout_observation_window` must not be longer than `lockout_duration`, unless the duration is `"never"`;
  - neither `min_password_age` nor `lockout_observation_window` may be `"never"`;
  - `min_password_length` must be 0-255, `password_history_count` 0-1024, and `lockout_threshold` 0-65535.
- When several policies apply to a user, the lowest `precedence` wins. AD requires every setting, so all attributes have defaults equal to the default domain policy.
- `applies_to` is authoritative and is applied after all objects exist.

## Attributes

<!-- schema:password_policy -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string | block label |  | Policy name (the CN). Defaults to the block label. |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `precedence` | number |  | required; LDAP `msDS-PasswordSettingsPrecedence` | Lower numbers win when several policies apply. |
| `min_password_length` | number | `7` | LDAP `msDS-MinimumPasswordLength` | Minimum password length. |
| `password_history_count` | number | `24` | LDAP `msDS-PasswordHistoryLength` | Number of remembered passwords. |
| `complexity_enabled` | bool | `true` | LDAP `msDS-PasswordComplexityEnabled` | Require complex passwords. |
| `reversible_encryption_enabled` | bool | `false` | LDAP `msDS-PasswordReversibleEncryptionEnabled` | Store passwords with reversible encryption. |
| `min_password_age` | string | `"1d"` | LDAP `msDS-MinimumPasswordAge` | Minimum password age. |
| `max_password_age` | string | `"42d"` | LDAP `msDS-MaximumPasswordAge` | Maximum password age, or "never". |
| `lockout_threshold` | number | `0` | LDAP `msDS-LockoutThreshold` | Failed logons before lockout (0 = never lock). |
| `lockout_duration` | string | `"30m"` | LDAP `msDS-LockoutDuration` | Lockout duration, or "never" (until an admin unlocks). |
| `lockout_observation_window` | string | `"30m"` | LDAP `msDS-LockoutObservationWindow` | Window for counting failed logons. |
| `applies_to` | list(string) |  | authoritative; DN or sAMAccountName; LDAP `msDS-PSOAppliesTo` | Users or global security groups (DN or sAMAccountName) the policy applies to. Authoritative. |
<!-- /schema -->

Also exposes `dn`.
