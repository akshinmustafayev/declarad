# ou

An organizational unit.

```hcl
ou "corp" {
  name        = "Corp"
  description = "Managed by declarad"
}

ou "sales" {
  name              = "Sales"
  path              = ou.corp.dn
  city              = "London"
  country           = "GB"
  managed_by        = group.sales_leads.dn
  block_inheritance = false
}
```

DN: `OU=<name>,<path>`. `path` defaults to the domain root.

## Behaviour

- **Protection.** New OUs get the "Protect object from accidental deletion" deny ACE, as ADUC does (`protected_from_accidental_deletion = true` by default). Like ADUC, the parent also gets "Deny Everyone Delete Child", which stays when protection is later lifted because sibling OUs may rely on it. declarad refuses to delete a protected OU: set the attribute to `false`, apply, then remove the OU. Moving a protected OU lifts and restores the protection automatically.
- **Renames and moves.** Changing `name` or `path` renames or moves the OU in place, and objects inside move with it. Managed children are tracked by GUID and are not "moved twice".
- **Deletion** fails if the OU still contains objects declarad doesn't manage. declarad never deletes unmanaged objects.
- `block_inheritance` sets "Block Inheritance" for Group Policy (`gPOptions`). Link GPOs with [`gpo_link`](gpo_link.md).

## Attributes

<!-- schema:ou -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | OU name (the OU= part of the DN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `display_name` | string |  | LDAP `displayName` | Display name. |
| `street_address` | string |  | LDAP `street` | Street address. |
| `city` | string |  | LDAP `l` | City. |
| `state` | string |  | LDAP `st` | State or province. |
| `postal_code` | string |  | LDAP `postalCode` | Postal code. |
| `country` | string |  | LDAP `c` | Two-letter country code. |
| `managed_by` | string |  | DN or sAMAccountName; LDAP `managedBy` | DN or sAMAccountName of the managing user or group. |
| `block_inheritance` | bool | `false` | LDAP `gPOptions` | Block Group Policy inheritance on this OU. |
| `protected_from_accidental_deletion` | bool | `true` |  | Deny 'Delete' and 'Delete subtree' to Everyone, like the ADUC checkbox. |
<!-- /schema -->

Also exposes `dn`.
