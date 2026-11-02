# group

A security or distribution group.

```hcl
group "engineering" {
  name        = "Engineering"
  path        = ou.groups.dn
  description = "All engineers"
  scope       = "global"
  category    = "security"
  members     = [user.jdoe.dn, user.asmith.dn, "svc-build"]   # DNs or sAMAccountNames
  managed_by  = user.jdoe.dn
}

group "all_staff" {
  name     = "All Staff"
  scope    = "universal"
  category = "distribution"
  email    = "all@corp.example"
  members  = flatten([for d in module.department : d.member_dns])
}

# Nest a group into groups declarad doesn't manage (additive).
group "helpdesk" {
  name   = "helpdesk"
  groups = ["Remote Desktop Users"]
}
```

DN: `CN=<name>,<path>`; `path` defaults to `CN=Users`.

## Behaviour

- **`members` is authoritative**: after apply the group contains exactly the listed members, and anyone added by hand is removed. Omit `members` to leave membership alone. Entries may be DNs or sAMAccountNames; names resolve to managed resources first, then to directory objects.
- **Membership is applied after all objects exist**, so groups can contain each other and members created in the same apply.
- **Merging with `groups`**: when a user, computer, contact, service account or group lists this group in its `groups`, and this group manages `members`, the object is merged into `members`, so the two declarations never fight.
- **Scope changes** follow AD's rules: global ↔ domain local goes through universal automatically.
- `groups` (nesting this group into others) is additive: only memberships declarad added are removed later.
- Large groups (more than 1500 members) are read completely through ranged retrieval.

## Attributes

<!-- schema:group -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | Group name (the CN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `sam_account_name` | string | computed | LDAP `sAMAccountName` | Pre-Windows 2000 name. Defaults to name. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `display_name` | string |  | LDAP `displayName` | Display name. |
| `email` | string |  | LDAP `mail` | E-mail address. |
| `notes` | string |  | LDAP `info` | Notes. |
| `scope` | string | `"global"` | one of `global`, `domain_local`, `universal` | global, domain_local or universal. |
| `category` | string | `"security"` | one of `security`, `distribution` | security or distribution. |
| `managed_by` | string |  | DN or sAMAccountName; LDAP `managedBy` | DN or sAMAccountName of the managing user or group. |
| `members` | list(string) |  | authoritative; DN or sAMAccountName; LDAP `member` | Authoritative member list (DNs or sAMAccountNames). Omit to leave membership unmanaged. |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
