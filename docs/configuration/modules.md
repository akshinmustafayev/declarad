# Modules

A module is a directory of `.dad` files. The directory you run declarad in is the *root module*; `module` blocks include *child modules*. Use modules to package a pattern once (a department, a branch site, a tiering model) and instantiate it many times.

## Calling a module

```hcl
module "department" {
  for_each = var.departments
  source   = "./modules/department"

  # Arguments set the module's variables.
  name    = title(each.key)
  parent  = ou.corp.dn
  people  = each.value.people
}
```

| Argument | Description |
|---|---|
| `source` | Where the module comes from (below). Must be a literal string. |
| `for_each`, `count` | Instantiate the module several times. |
| `depends_on` | Everything in the module is applied after these resources/modules. |
| `connections` | Map the module's connection names to the caller's: `{ default = connection.eu }`. |
| anything else | Values for the module's variables. Unknown names and missing required variables are errors. |

## Writing a module

```hcl
# modules/department/main.dad
variable "name" { type = string }
variable "parent" { type = string }
variable "people" {
  type = list(object({ sam = string, first = string, last = string }))
}

ou "department" {
  name = var.name
  path = var.parent
}

user "person" {
  for_each         = { for p in var.people : p.sam => p }
  sam_account_name = each.key
  given_name       = each.value.first
  surname          = each.value.last
  path             = ou.department.dn
}

output "ou_dn" {
  value = ou.department.dn
}

output "member_dns" {
  value = values(user.person)[*].dn
}
```

A module may not contain `connection`, `declarad` or `import` blocks. It uses the connections the caller gives it.

## Addresses

Resources in modules get prefixed addresses:

```
module.department["engineering"].user.person["jdoe"]
module.department["engineering"].module.nested.group.x
```

Use these with `--target`, `state` commands and `moved` blocks.

## Using module outputs

```text
module.department["engineering"].ou_dn           # one instance
{ for k, d in module.department : k => d.ou_dn } # all instances (map for for_each, list for count)
flatten([for d in module.department : d.member_dns])
```

A resource that uses a module output depends on whatever produced that output.

## Sources

The syntax is Terraform's:

| Kind | Example |
|---|---|
| Local directory | `./modules/department`, `../shared/site` |
| Git (HTTPS) | `git::https://git.corp.local/ad/modules.git` |
| Git (SSH) | `git::ssh://git@git.corp.local/ad/modules.git`, `git@github.com:corp/ad-modules.git` |
| GitHub/Bitbucket shorthand | `github.com/corp/ad-modules`, `bitbucket.org/corp/ad-modules` |
| Archive | `https://artifacts.corp.local/ad/department-1.4.0.zip`, `.tar.gz`, `.tgz`, `.tar`, or `?archive=zip` |

Modifiers:

- `//subdir` selects a directory inside the repository or archive: `git::https://host/repo.git//modules/department`.
- `?ref=` selects a branch, tag or commit for git: `?ref=v1.4.0`. Pin to tags in production.
- `?depth=1` makes a shallow clone (with `ref`, it must be a branch or tag).

Registry sources (`namespace/name/provider`), `hg::`, `s3::` and `gcs::` are not supported.

### Installing

Remote modules are downloaded by `declarad init` into `.declarad/modules/<module path>` and listed in `.declarad/modules/modules.json`. Other commands use the installed copy:

- A remote source that was never installed → error "Run 'declarad init'".
- A changed source (for example a new `ref`) → error until you run `declarad init`.
- `declarad init --upgrade` downloads every remote module again (e.g. to follow a branch).

git uses your normal credentials (SSH agent, credential helper) and never prompts. Relative `source` paths inside a remote module resolve within the downloaded copy, and remote modules may call further remote modules.

Commit the configuration, not `.declarad/`.
