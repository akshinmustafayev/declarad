# Syntax and expressions

declarad uses HCL native syntax, the language of Terraform, with Terraform's conventions. Files end in `.dad`.

## Structure

```hcl
# Comments: #, // or /* ... */

block_type "label" {          # a block with one label
  attribute = expression       # one attribute per line
  nested_block {               # e.g. lifecycle
    other = true
  }
}
```

Top-level blocks:

| Block | Purpose |
|---|---|
| `declarad { ... }` | Settings: `required_version`, `backend`. Root module only. See [backends](backends.md). |
| `connection ["name"] { ... }` | A directory to manage. See [connections](connections.md). |
| `variable "name" { ... }` | An input. See [variables](variables-locals-outputs.md). |
| `locals { ... }` | Named values. |
| `output "name" { ... }` | A result shown after apply and readable by parent modules. |
| `module "name" { ... }` | Includes another module. See [modules](modules.md). |
| `data "type" "name" { ... }` | Reads an existing object. See [data sources](data-sources.md). |
| `moved { ... }`, `import { ... }` | Refactoring and adoption. See [moved and import](moved-and-import.md). |
| `<type> "name" { ... }` or `resource "<type>" "name" { ... }` | A resource. See the [resource reference](../README.md#resource-reference). |

Attribute names and labels are identifiers: letters, digits, `_` and `-`, not starting with a digit.

## Types and literals

| Type | Examples |
|---|---|
| string | `"text"`, `"tab\tnewline\n"`, `"é"`, heredocs |
| number | `42`, `-1`, `3.14`, `1e3` |
| bool | `true`, `false` |
| list (also used for sets and tuples) | `["a", "b"]`, `[1, 2, 3,]` (trailing comma allowed; newlines allowed) |
| object (also used for maps) | `{ name = "x", "key with spaces" = 1, (var.dynamic_key) = 2 }` |
| null | `null`: "not set"; the default applies |

String escapes: `\n \r \t \" \\ \uNNNN \UNNNNNNNN`. To write a literal `${` or `%{`, double the first character: `$${`, `%%{`.

### Heredocs

```hcl
description = <<EOT
Line one
Line two
EOT

notes = <<-EOT
    Leading indentation common to all lines
    is removed with <<-.
  EOT
```

## References

| Expression | Value |
|---|---|
| `var.name` | Variable value. |
| `local.name` | Local value. |
| `<type>.<name>.<attr>` | Attribute of a resource, e.g. `ou.staff.dn`, `user.jdoe.user_principal_name`. |
| `<type>.<name>[key].<attr>` | One instance of a `for_each`/`count` resource. |
| `<type>.<name>` | Whole object (or map/list of instances). |
| `data.<type>.<name>.<attr>` | Data source result. |
| `module.<name>.<output>` | Module output; `module.<name>[key].<output>` for expanded modules. |
| `each.key`, `each.value` | Inside a `for_each` block. |
| `count.index` | Inside a `count` block. |
| `path.module`, `path.root`, `path.cwd` | Directories. |
| `connection.<name>.domain`, `.base_dn`, `.forest_base_dn`, `.server`, `.name` | Connection facts (`connection.default` for the unlabeled one). |

Every resource exposes `dn` besides its schema attributes. Referencing a resource creates a dependency: it is applied first. References from deferred attributes (`members`, `groups`, `manager`, ...) don't order objects, because those attributes are applied after every object exists.

## Operators

From lowest to highest precedence:

| Operators | Meaning |
|---|---|
| `c ? a : b` | Conditional |
| `\|\|` | Or (short-circuit) |
| `&&` | And (short-circuit) |
| `==`, `!=` | Equality (no type conversion: `1 == "1"` is false) |
| `<`, `<=`, `>`, `>=` | Numeric comparison |
| `+`, `-` | Addition, subtraction |
| `*`, `/`, `%` | Multiplication, division, remainder |
| `!x`, `-x` | Not, negation |

Arithmetic converts numeric strings (`"5" + 1` is `6`). An operator may end a line and continue on the next.

## Indexing and splat

```text
local.list[0]                    # list element
local.map["key"]                 # map element (also local.map.key)
local.users[*].sam               # attribute of every element
values(user.staff)[*].dn         # DNs of all for_each instances
```

## for expressions

```text
[for u in var.users : upper(u)]                             # list
[for u in var.users : u.sam if u.enabled]                   # filtered
{ for u in var.users : u.sam => u }                         # object keyed by sam
{ for u in var.users : u.department => u.sam... }           # grouping: department => [sams]
[for k, v in var.map : "${k}=${v}"]                         # key and value
```

Over a list, the key is the index; over an object, the key is the attribute name. Without `...`, duplicate keys are an error.

## Templates

Strings interpolate expressions and support directives:

```hcl
description = "Managed by ${var.team} (${length(var.members)} members)"

notes = <<-EOT
  %{ for m in var.members ~}
  - ${m}
  %{ endfor ~}
  %{ if var.critical }CRITICAL%{ else }standard%{ endif }
  EOT
```

`~` next to a delimiter trims whitespace (including newlines) on that side. A string consisting of a single interpolation (`"${var.list}"`) yields the value itself, not a string.

`templatefile("file.tpl", { ... })` renders a template stored in a file.

## Functions

Calls look like `name(arg1, arg2)`. `f(list...)` expands a list into arguments. See [functions](functions.md).

## Formatting

`declarad fmt` rewrites files canonically. Run it before committing; `declarad fmt --check` in CI.
