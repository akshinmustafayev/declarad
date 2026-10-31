# Resource meta-arguments

These work on every resource type. `for_each`, `count`, `depends_on` and `connection` also work on `data` blocks, and `for_each`, `count`, `depends_on` on `module` blocks.

## for_each

Creates one instance per element of a map or a set of strings.

```hcl
user "staff" {
  for_each = { for r in csvdecode(file("staff.csv")) : r.sam => r }

  sam_account_name = each.key
  given_name       = each.value.first
  surname          = each.value.last
  path             = ou.staff.dn
}

group "team" {
  for_each = toset(["eng", "ops", "finance"])
  name     = "Team ${title(each.key)}"
}
```

Instances are addressed `user.staff["jdoe"]`. Use stable keys (sAMAccountName, not list positions): adding or removing an element then only affects that element.

Inside the block, `each.key` is the key and `each.value` the element (for a set, both are the string). Without `for_each`/`count`, the label attribute (`name`, or `sam_account_name` for users) defaults to the block label. With them it doesn't, so set it explicitly.

A list of objects can't be used directly. Build a map: `{ for u in local.rows : u.sam => u }`.

## count

Creates `count` numbered instances: `computer.ws[0]`, `computer.ws[1]`, ...

```hcl
computer "kiosk" {
  count = var.kiosk_count
  name  = format("KIOSK%02d", count.index + 1)
}
```

Removing an element from the middle renumbers the rest, which renames objects. Prefer `for_each` unless the instances are interchangeable.

## depends_on

Adds ordering that references don't imply:

```hcl
user "svc" {
  depends_on = [password_policy.service_accounts, module.base]
}
```

Entries are resources or modules (all resources inside). References and containment (an object after the OU it's placed in) are tracked automatically, so `depends_on` is rarely needed.

## connection

Selects which directory the object lives in. See [connections](connections.md).

```hcl
group "eu_admins" {
  connection = connection.eu
}
```

## lifecycle

```hcl
group "domain_tier0" {
  lifecycle {
    prevent_destroy = true            # any plan deleting it fails
    ignore_changes  = [description]   # or: ignore_changes = all

    precondition {
      condition     = startswith(var.environment, "prod")
      error_message = "Tier 0 groups only exist in production."
    }
  }
}
```

| Setting | Effect |
|---|---|
| `prevent_destroy` | Deleting the resource (by removing it or by `destroy`) fails the plan. |
| `ignore_changes` | Listed attributes are set at creation and never compared afterwards. `all` ignores every attribute. With `name` or `path`, an object renamed or moved outside declarad stays where it is, even when other attributes are updated. |
| `precondition` | Checked when the configuration is evaluated; a false condition is an error. `each`/`count` are available. |
