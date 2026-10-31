# group_membership

Adds members to an existing group without managing the whole group: typically built-in or other teams' groups such as Domain Admins.

```hcl
data "group" "domain_admins" {
  sam_account_name = "Domain Admins"
}

group_membership "tier0" {
  group   = data.group.domain_admins.dn          # or "Domain Admins"
  members = [group.tier0_admins.dn, user.breakglass.dn]
}
```

## Behaviour

- **Additive.** Listed members are added; existing members are left alone. State records which members this resource added; a member is removed only when you remove it from `members` and it was added by declarad.
- **Removing the resource** removes only the members it added. Members that were already in the group (for example Administrator in Domain Admins) stay.
- **Changing `group`** removes the members from the old group and adds them to the new one.
- Applied after all objects exist, so the members may be created in the same apply.
- If the target is a `group` resource that manages `members`, the listed members are merged into that authoritative list, so the two never fight.

This is an *attachment*: it changes an object it doesn't own, so it needs no import.

## Attributes

<!-- schema:group_membership -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `group` | string |  | required; DN or sAMAccountName | The group (DN or sAMAccountName). |
| `members` | list(string) |  | required; additive; DN or sAMAccountName | Members to add (DNs or sAMAccountNames). |
<!-- /schema -->
