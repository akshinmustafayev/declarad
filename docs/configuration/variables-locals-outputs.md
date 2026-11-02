# Variables, locals and outputs

## Variables

```hcl
variable "departments" {
  description = "Departments and their people."
  type = map(object({
    manager = string
    people = list(object({
      sam   = string
      admin = optional(bool, false)
    }))
  }))
  default  = {}
  nullable = false

  validation {
    condition     = alltrue([for d in var.departments : length(d.people) > 0])
    error_message = "Every department needs at least one person."
  }
}
```

| Attribute | Purpose |
|---|---|
| `type` | Type constraint (below). Values are converted, e.g. `"5"` to `5`, and mismatches are errors. |
| `default` | Value when none is given. A variable without a default is required. |
| `description` | Documentation. |
| `sensitive` | Values computed from the variable are shown as `(sensitive value)` in plans, and an output exposing them must itself be `sensitive = true`. |
| `nullable` | With `false`, a `null` value uses the default instead. |
| `validation { condition, error_message }` | Repeatable checks; `error_message` may interpolate the value. |

### Type constraints

| Constraint | Accepts |
|---|---|
| `string`, `number`, `bool` | Primitives (strings convert to numbers/bools when they look like them). |
| `any` | Anything. |
| `list(T)`, `set(T)`, `map(T)` | Collections. Sets remove duplicates; string and number sets are sorted. |
| `tuple([T1, T2])` | Fixed-length list. |
| `object({ a = T, b = optional(T, default) })` | Objects; extra attributes are dropped, missing optional ones get the default (or `null`). |

### Setting root variables

In order of precedence: `--var name=value` on the command line, the environment variable `DECLARAD_VAR_name`, then `default`. For `string`, `number` and `bool` variables (or untyped ones with such a default) the text is converted (`--var n=3`, `--var flag=true`). For collections it is parsed as HCL (`--var 'users=["a","b"]'`). `--var` for a variable that isn't declared is an error.

Module variables are set by the arguments of the `module` block.

## Locals

```hcl
locals {
  base_dn    = connection.default.base_dn
  rows       = csvdecode(file("users.csv"))
  users      = { for r in local.rows : r.sam => r }
  by_dept    = { for r in local.rows : r.department => r.sam... }
  admin_sams = [for r in local.rows : r.sam if r.admin == "yes"]
}
```

Locals can reference variables, other locals, resources, data sources and module outputs. Several `locals` blocks are allowed; names must be unique.

## Outputs

```hcl
output "team_ous" {
  description = "OU of each team"
  value       = { for k, m in module.team : k => m.ou_dn }
}

output "service_account_password" {
  value     = var.initial_password
  sensitive = true          # shown as (sensitive value), never stored in state
}
```

Root outputs are shown after `plan` and `apply`, stored in state (except sensitive ones) and read with `declarad output`. Module outputs are how a parent reads values from a child: `module.team.ou_dn`. An output whose value comes from a sensitive variable (directly or through locals and modules) must be declared `sensitive = true`.

Outputs may have `depends_on` and `precondition { condition, error_message }` blocks.
