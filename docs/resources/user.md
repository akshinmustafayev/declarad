# user

A user account.

```hcl
user "jdoe" {
  given_name       = "John"
  surname          = "Doe"
  path             = ou.staff.dn
  title            = "Engineer"
  department       = "Engineering"
  email            = "john.doe@corp.example"
  manager          = user.asmith.dn
  groups           = ["VPN Users", group.engineering.dn]
  initial_password = env("JDOE_INITIAL_PASSWORD")
  change_password_at_logon = true

  lifecycle {
    ignore_changes = [phone, mobile]       # maintained by the helpdesk
  }
}
```

DN: `CN=<name>,<path>`. Defaults:
- `sam_account_name`: the block label.
- `display_name`: "given_name surname".
- `name`: `display_name`, then `sam_account_name`.
- `user_principal_name`: `sam@domain`.
- `path`: `CN=Users`.

## Behaviour

- **Creation.** Accounts are created disabled. The password is set (LDAPS or StartTLS required), `pwdLastSet` is set when `change_password_at_logon = true`, then the account is enabled. If any of this fails (e.g. the password violates policy), the new account is deleted again and the error explains why.
- **An enabled user needs `initial_password` at creation** (or `enabled = false`). The password is never read back, compared or stored. Changing it later has no effect. Reset passwords outside declarad.
- **`enabled`** and **`password_never_expires`** manage bits of `userAccountControl`; other bits are preserved.
- **`account_expires`** takes a date `YYYY-MM-DD` (end of that day UTC) or `"never"`.
- **`groups` is additive**: listed groups are joined, and a membership is removed only if declarad added it earlier and you removed it from the list. Primary group membership (Domain Users) is invisible to `memberOf` and can't be managed here.
- **Renames.** Changing `name` renames the object (the CN); `display_name` and `sam_account_name` are ordinary attributes.
- **`manager`** is applied after all objects exist, so two users may reference each other.

## Attributes

<!-- schema:user -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `sam_account_name` | string | block label | LDAP `sAMAccountName` | Logon name (pre-Windows 2000). Defaults to the block label. |
| `name` | string | computed |  | Object name (the CN). Defaults to display_name, then sam_account_name. |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `user_principal_name` | string | computed | LDAP `userPrincipalName` | UPN. Defaults to sam_account_name@domain. |
| `given_name` | string |  | LDAP `givenName` | First name. |
| `surname` | string |  | LDAP `sn` | Last name. |
| `initials` | string |  | LDAP `initials` | Initials. |
| `display_name` | string | computed | LDAP `displayName` | Display name. Defaults to "given_name surname" when either is set. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `email` | string |  | LDAP `mail` | E-mail address. |
| `title` | string |  | LDAP `title` | Job title. |
| `department` | string |  | LDAP `department` | Department. |
| `company` | string |  | LDAP `company` | Company. |
| `office` | string |  | LDAP `physicalDeliveryOfficeName` | Office. |
| `phone` | string |  | LDAP `telephoneNumber` | Telephone number. |
| `mobile` | string |  | LDAP `mobile` | Mobile number. |
| `employee_id` | string |  | LDAP `employeeID` | Employee ID. |
| `employee_number` | string |  | LDAP `employeeNumber` | Employee number. |
| `employee_type` | string |  | LDAP `employeeType` | Employee type. |
| `street_address` | string |  | LDAP `streetAddress` | Street address. |
| `city` | string |  | LDAP `l` | City. |
| `state` | string |  | LDAP `st` | State or province. |
| `postal_code` | string |  | LDAP `postalCode` | Postal code. |
| `country` | string |  | LDAP `c` | Two-letter country code. |
| `home_directory` | string |  | LDAP `homeDirectory` | Home folder UNC path. |
| `home_drive` | string |  | LDAP `homeDrive` | Home drive letter, e.g. H:. |
| `script_path` | string |  | LDAP `scriptPath` | Logon script. |
| `profile_path` | string |  | LDAP `profilePath` | Roaming profile path. |
| `web_page` | string |  | LDAP `wWWHomePage` | Web page. |
| `proxy_addresses` | list(string) |  | authoritative; LDAP `proxyAddresses` | E-mail proxy addresses (e.g. SMTP:jdoe@corp.com). Authoritative when set. |
| `account_expires` | string |  |  | Account expiry date (YYYY-MM-DD, UTC) or "never". |
| `manager` | string |  | DN or sAMAccountName; LDAP `manager` | DN or sAMAccountName of the user's manager. |
| `enabled` | bool | `true` |  | Whether the account is enabled. |
| `password_never_expires` | bool | `false` |  | Set 'Password never expires'. |
| `initial_password` | string |  | creation only; sensitive | Password set when the account is created. Never read back or compared. |
| `change_password_at_logon` | bool | `false` | creation only | Require a password change at first logon (applied at creation only). |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
