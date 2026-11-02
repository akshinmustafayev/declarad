# Bulk users from HR data

HR systems usually export people as CSV. declarad can turn that file directly into users, OUs and groups.

`people.csv`:

```csv
sam,first_name,last_name,title,department,office,manager,active
jdoe,John,Doe,Head of Engineering,Engineering,London,,yes
asmith,Anna,Smith,Engineer,Engineering,London,jdoe,yes
bkhan,Bilal,Khan,CFO,Finance,Boston,,yes
mold,Mark,Old,Analyst,Finance,Boston,bkhan,no
```

```hcl
locals {
  rows        = csvdecode(file("people.csv"))
  people      = { for r in local.rows : r.sam => r }
  departments = toset([for r in local.rows : r.department])
  by_dept     = { for r in local.rows : r.department => r.sam... if r.active == "yes" }
}

ou "people" {
  name = "People"
}

ou "leavers" {
  name = "Leavers"
}

user "person" {
  for_each = local.people

  sam_account_name = each.key
  given_name       = each.value.first_name
  surname          = each.value.last_name
  title            = each.value.title
  department       = each.value.department
  office           = each.value.office
  email            = "${each.key}@corp.example"
  manager          = each.value.manager == "" ? null : user.person[each.value.manager].dn

  # Leavers are disabled and moved, not deleted.
  enabled = each.value.active == "yes"
  path    = each.value.active == "yes" ? ou.people.dn : ou.leavers.dn

  initial_password         = var.initial_password
  change_password_at_logon = true
}

group "department" {
  for_each = local.departments
  name     = "Dept ${each.key}"
  path     = ou.people.dn
  members  = [for sam in lookup(local.by_dept, each.key, []) : user.person[sam].dn]
}
```

Notes:

- **Use stable keys.** `for_each` keys (`sam`) identify users in state, so changing a row's other columns updates the user in place.
- **Removing a row deletes the user.** To keep leavers, mark them inactive (as above) instead of removing rows.
- **Grouping**: `r.department => r.sam...` collects all sams of a department into a list.
- **The manager column** references another user by key; managers are applied after all users exist, so ordering doesn't matter.
- **Validate data** with a variable `validation` block or a resource `precondition`, e.g. that every `manager` value exists:

  ```hcl
lifecycle {
  precondition {
    condition     = each.value.manager == "" || contains(keys(local.people), each.value.manager)
    error_message = "Unknown manager ${each.value.manager} for ${each.key}."
  }
}
```

See `examples/csv-users` and `examples/hr-sync` for complete configurations.
