# declarad

Declarative, Terraform-style management of **Active Directory**: describe users, groups, OUs, computers, service accounts, password policies, GPO links and DNS zones and records in HCL, review a `plan`, then `apply` it. State can live locally or in S3, Azure Blob Storage or an HTTP backend; modules can come from git; one configuration can manage several domains.

declarad talks to domain controllers directly over **LDAP/LDAPS** (`System.DirectoryServices.Protocols`), so it runs on Windows, Linux and macOS without RSAT, PowerShell or WinRM.

```hcl
connection {
  server   = "dc01.corp.local"
  domain   = "corp.local"
  username = "svc-declarad@corp.local"
  password = env("DECLARAD_PASSWORD")
}

ou "staff" {
  name = "Staff"
}

user "jdoe" {
  given_name       = "John"
  surname          = "Doe"
  path             = ou.staff.dn
  initial_password = env("JDOE_PASSWORD")
}

group "engineering" {
  name    = "Engineering"
  path    = ou.staff.dn
  members = [user.jdoe.dn]
}
```

```
$ declarad plan
  # user.jdoe will be updated in-place
  ~ user.jdoe {
      ~ title = "Engineer" -> "Lead"
    }
Plan: 0 to add, 1 to change, 0 to destroy.
```

**Documentation:** [docs/](docs/README.md) has the full guide and reference (concepts, CLI, language, functions, modules, backends, every resource type, guides). [examples/](examples/README.md) has runnable configurations.

## Build

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test
dotnet publish src/Declarad -c Release -o out   # produces out/declarad
```

Release zips for Windows, Linux and macOS (x64 and arm64, self-contained or framework-dependent) are built with `build/build.sh` or `build/build.ps1`; see [build/README.md](build/README.md).

## Commands

| Command | Purpose |
|---|---|
| `declarad init [--migrate-state] [--reconfigure] [--upgrade]` | Install modules, configure the state backend (migrating existing state), and create a starter `main.dad` if the directory is empty |
| `declarad validate` | Parse and check the configuration offline (data sources are treated as unknown) |
| `declarad fmt [paths] [--check] [--diff] [-r]` | Rewrite files in canonical format; `--check` exits 3 if anything would change |
| `declarad plan [--adopt] [--target addr] [--detailed-exitcode]` | Show what would change. With `--detailed-exitcode`: 0 = no changes, 1 = error, 2 = changes (for CI drift detection) |
| `declarad apply [--auto-approve] [--adopt] [--target addr]` | Plan, confirm, then apply |
| `declarad destroy [--auto-approve] [--target addr]` | Delete everything this configuration manages |
| `declarad import <address> <dn-or-guid>` | Start managing an existing object (or use an `import` block) |
| `declarad export [--base DN] [--type ...] [-o file]` | Generate configuration from existing objects |
| `declarad output [name] [--json]` | Show outputs from the last apply |
| `declarad graph` | Dependency graph in Graphviz DOT format |
| `declarad schema [type]` | Resource types, data sources, their attributes, and the function list |
| `declarad state list \| show \| rm \| mv \| pull \| push` | Inspect or edit the state |
| `declarad force-unlock <lock-id>` | Release a lock left by a crashed run |

Global options:
- `-chdir=DIR`, before the subcommand as in Terraform (`declarad -chdir=environments/prod plan`): run in another directory (alias `-C DIR`).
- `--var name=value`: set a variable.
- `--backend-config key=value|file`: override backend settings.
- `--state <file>`: use a local state file instead of the configured backend.
- `--lock=false`, `--lock-timeout 30s`: control state locking.
- `-v` (debug logging), `--no-color`.

`--target` behaves as in Terraform. A targeted plan also includes everything the targets depend on: references, including members, and the OUs that contain them. A targeted destroy also includes everything that depends on the targets.

All `*.dad` files in a directory are loaded together.

## How it works

1. **Configuration** is parsed by a built-in HCL parser and evaluated lazily, so `ou.staff.dn` only evaluates that OU's `name` and `path`. Distinguished names are computed from `name` + `path`, so references resolve at plan time. Data sources are read when the configuration is loaded.
2. **State** (`declarad.state.json`) maps each resource address to the object's `objectGUID`. Objects are found by GUID, so renames and moves are tracked, and objects removed from the configuration can be deleted. The state holds no passwords.
3. **Plan** reads every managed object, diffs it against the configuration and orders changes:
   - creates and updates first, in dependency order (references, `depends_on`, and parent containers before children);
   - then deferred attributes (`members`, `groups`, `manager`, `managed_by`, `applies_to`…) and attachments (`group_membership`, `gpo_link`), once every object exists, so mutual references never cycle;
   - then deletes: attachments are detached first, then objects are deleted deepest DN first.
4. **Apply** executes the plan and saves state after every step, so an interrupted run leaves an accurate record. A lock file prevents concurrent runs.

### Attribute ownership

- An attribute is managed while it is set in the configuration (defaults count). If you remove it, the next apply clears the value declarad wrote. Attributes you never set are left alone, so helpdesk-maintained fields are safe.
- `group.members`, `password_policy.applies_to`, SPN lists and `principals_allowed_to_retrieve_password` are **authoritative**: values not listed are removed. Omit the attribute to leave it unmanaged.
- `groups` (on users, computers, contacts and service accounts) and `group_membership` are **additive**: listed memberships are added, and a membership is removed only if declarad added it earlier. An object listing a group whose `members` is managed is merged into that list, so the two declarations never conflict.
- `lifecycle { ignore_changes = [phone] }` (or `= all`) stops managing attributes after creation.

### Safety

- An object that already exists but isn't in state stops the plan with an error. Bring it under management with `declarad import`, an `import` block, or `--adopt`. Adopted objects are deleted if you later remove them from the configuration.
- `lifecycle { prevent_destroy = true }` makes any plan that would delete the resource fail.
- OUs are protected from accidental deletion by default, like ADUC. Deleting one requires setting `protected_from_accidental_deletion = false` first. Moves lift and restore the protection automatically.
- A new user's `initial_password` is applied only at creation. If setting it fails (for example on password policy), the half-created account is removed again.
- Renaming a block label (`user "a"` → `user "b"`) for the same object moves its state entry rather than recreating the object. For anything else, such as moving resources into a module, use `moved` blocks.

## Language

declarad implements HCL's native syntax, with Terraform's conventions.

**Blocks:** `declarad` (settings and backend), `connection`, `variable`, `locals`, `output`, `module`, `data`, `moved`, `import`, and resources, written either as `user "jdoe" { }` or Terraform-style `resource "user" "jdoe" { }`.

**Expressions:**
- literals, including fractional numbers;
- strings with `${}` interpolation and `%{ if }` / `%{ for }` directives with `~` strip markers;
- heredocs (`<<EOT`, `<<-EOT`);
- lists and objects;
- arithmetic `+ - * / %`, comparison `< <= > >= == !=`, logic `&& || !`, and `cond ? a : b`;
- `for` expressions with filters (`[for u in list : u.dn if u.enabled]`) and grouping (`{ for r in rows : r.dept => r.sam... }`);
- splat (`list[*].dn`, `values(user.staff)[*].dn`) and argument expansion (`max(numbers...)`).

**References:** `var.x`, `local.x`, `each.key` / `each.value`, `count.index`, `path.module` / `path.root` / `path.cwd`, `connection.<name>.domain` / `base_dn` / `server`, `data.<type>.<name>.<attr>`, `module.<name>.<output>`, `<type>.<name>.<attr>`, with `[key]` for `for_each`/`count` instances.

**Functions** (run `declarad schema` for the full list), following Terraform's standard library:
- strings: `format`, `formatlist`, `join`, `split`, `replace` (with `/regex/`), `regex`, `regexall`, `substr`, `title`, `trim*`;
- collections: `merge`, `flatten`, `distinct`, `setunion` and the other set functions, `zipmap`, `lookup`, `range`, `chunklist`;
- numbers and types: `min`, `max`, `sum`, `tonumber`, `toset`, and the other type conversions;
- error handling: `try`, `can`;
- encoding and hashing: `jsonencode` / `jsondecode`, `csvdecode`, `base64*`, hashes;
- files: `file`, `templatefile`, `fileset`, `fileexists`;
- plus `env(name, [default])` and the DN helpers `dnjoin`, `dnparent`, `dnvalue`, `dnescape`, `domaindn` and `filterescape`.

**Meta-arguments** on resources: `for_each` (a map, or a set of strings), `count`, `depends_on`, `connection`, and `lifecycle { prevent_destroy, ignore_changes, precondition { condition, error_message } }`.

**Variables:** `type` constraints (`string`, `number`, `bool`, `list(T)`, `set(T)`, `map(T)`, `tuple([...])`, `object({ a = string, b = optional(bool, false) })`), `default`, `nullable`, `sensitive`, and `validation { condition, error_message }` blocks. Values come from `default`, `--var name=value`, or `DECLARAD_VAR_<name>`.

### Modules

```hcl
module "department" {
  for_each = var.departments
  source   = "./modules/department"
  name     = title(each.key)
  parent   = ou.corp.dn
  people   = each.value.people
}

group "all_staff" {
  members = flatten([for d in module.department : d.member_dns])
}
```

Modules can use `for_each`, `count`, `depends_on` and `connections`, and can call other modules. Resources inside get addresses like `module.department["eng"].user.person["jdoe"]`. Connections are defined in the root module. Child modules see the caller's connections by the same names; pass different ones with `connections = { default = connection.eu }`.

Module sources use Terraform's syntax. `declarad init` downloads remote modules into `.declarad/modules`, and `init --upgrade` fetches them again:

| Source | Example |
|---|---|
| Local path | `./modules/department` |
| Git over HTTPS or SSH | `git::https://git.corp.local/ad/modules.git//department?ref=v1.4.0`, `git::ssh://git@host/ad/modules.git` |
| GitHub / Bitbucket shorthand | `github.com/corp/ad-modules//department?ref=main` |
| SCP-style git | `git@github.com:corp/ad-modules.git//department` |
| Archive over HTTP(S) | `https://artifacts.corp.local/ad/department-1.4.0.zip`, `https://host/m?archive=tar.gz` |

`//subdir` selects a directory inside the repository or archive. `?ref=` takes a branch, tag or commit, and `?depth=` makes a shallow clone. git uses your normal credentials (SSH agent, credential helper).

### Multiple connections

```hcl
connection {                 # "default"
  server = "dc01.hq.corp.local"
  domain = "hq.corp.local"
}

connection "eu" {
  server = "dc01.eu.corp.local"
  domain = "eu.corp.local"
}

group "eu_admins" {
  connection = connection.eu
}

module "eu_offices" {
  source      = "./modules/office"
  connections = { default = connection.eu }
}
```

Each object lives in exactly one connection's directory; that is recorded in state, and objects cannot move between connections. Data sources take `connection` too.

### State backends

```hcl
declarad {
  required_version = ">= 0.3"

  backend "s3" {
    bucket = "corp-declarad-state"
    key    = "ad/prod.tfstate"
    region = "eu-west-1"
  }
}
```

| Backend | Settings | Locking |
|---|---|---|
| `local` (default) | `path` | `<path>.lock.info` file |
| `s3` | `bucket`, `key`, `region`, `endpoint`, `use_path_style`, `encrypt`, `kms_key_id`, `profile`, `access_key` / `secret_key` / `token` (default: the AWS credential chain) | `<key>.tflock` object written with a conditional put (Terraform's `use_lockfile`) |
| `azurerm` | `storage_account_name`, `container_name`, `key`, `access_key` (`ARM_ACCESS_KEY`), `sas_token` (`ARM_SAS_TOKEN`), `connection_string`, `use_azuread_auth`, `endpoint` | Blob lease, with lock info in the blob metadata |
| `http` | `address`, `update_method`, `lock_address`, `lock_method`, `unlock_address`, `unlock_method`, `username`, `password`, `skip_cert_verification` (and `TF_HTTP_*` variables) | `LOCK` / `UNLOCK` requests, as in Terraform's http backend |

The backend block may use `env()` but not variables. Settings can be supplied or overridden with `--backend-config key=value` or `--backend-config prod.backend.hcl`. After changing the backend, run `declarad init --migrate-state` to copy the state, or `--reconfigure` to start fresh. Other commands refuse to run until you do. Without a lock, a save checks lineage and serial, so a concurrent run cannot silently overwrite newer state. The state file format is unchanged across backends (`state pull` / `state push`).

### moved and import blocks

```hcl
moved {
  from = ou.finance
  to   = module.department["finance"].ou.department
}

import {
  to = group.legacy
  id = "CN=Legacy,OU=Groups,DC=corp,DC=local"   # DN or objectGUID
}
```

### Data sources

Data sources read existing objects without managing them. They are read when the configuration loads; `validate` treats their values as unknown.

| Data source | Arguments | Exposes |
|---|---|---|
| `user`, `group`, `ou`, `computer`, `contact`, `container`, `service_account`, `password_policy` | exactly one of `dn`, `guid`, `name`, `sam_account_name`; optional `base` | every attribute of the type, plus `dn`, `guid`, `sid` |
| `gpo` | `name` or `id` | `id` (`{GUID}`), `dn`, `name`, `file_sys_path`, `status` |
| `domain` | — | `dn`, `name`, `sid` |
| `objects` | `filter`, optional `base`, `scope`, `attributes` | `objects` (list), `dns` (list) |

## Resource types

Run `declarad schema <type>` for every attribute with its type and default.

| Type | Notes |
|---|---|
| `ou` | `name` (label), `path` (default: domain root), address fields, `managed_by`, `block_inheritance`, `protected_from_accidental_deletion` (default `true`) |
| `container` | `name`, `path`, `description` |
| `group` | `name`, `sam_account_name`, `scope` (`global`/`domain_local`/`universal`; conversions go through universal when AD requires it), `category`, `email`, `managed_by`, `members` (authoritative), `groups` (nesting, additive) |
| `user` | `sam_account_name` (label), names, contact and organization fields, `proxy_addresses`, `account_expires`, `manager`, `enabled`, `password_never_expires`, `initial_password` / `change_password_at_logon` (creation only), `groups` |
| `computer` | `name` (label, ≤15 chars), `sam_account_name` (default `NAME$`), `path` (default `CN=Computers`), `dns_host_name`, `location`, `service_principal_names`, `enabled`, `groups`. Pre-staged; the machine sets its password when it joins |
| `contact` | `name`, names, mail and address fields, `proxy_addresses`, `groups` |
| `service_account` | gMSA. `name` (label), `dns_host_name`, `service_principal_names`, `kerberos_encryption_types`, `password_interval_days` (creation only), `principals_allowed_to_retrieve_password`, `enabled`, `groups`. Needs a KDS root key and an encrypted connection |
| `password_policy` | Fine-grained password policy: `precedence` (required), `min_password_length`, `password_history_count`, `complexity_enabled`, `reversible_encryption_enabled`, `min_password_age`, `max_password_age`, `lockout_threshold`, `lockout_duration`, `lockout_observation_window` (durations such as `"42d"`, `"30m"`, `"never"`), `applies_to` |
| `group_membership` | Attachment: adds `members` to an existing `group` (DN or sAMAccountName), e.g. Domain Admins. Additive; detaches only what it added |
| `gpo_link` | Attachment: links an existing GPO (`gpo` = `{GUID}` or `data.gpo.x.id`) to `target` (OU or domain DN), with `enabled`, `enforced`, `order` (1 = highest precedence) |
| `dns_zone` | AD-integrated zone. `name` (label), `replication_scope` (`domain`, `forest` or `legacy`; immutable), `dynamic_update` (`none`/`secure`/`nonsecure_and_secure`), SOA fields (`primary_server` defaulting to the connection's server, `admin_email`, `refresh`, `retry`, `expire`, `minimum_ttl`, `ttl`), `name_servers`. Deleting a zone deletes all its records |
| `dns_record` | One record set (name + type) in a zone, managed or not: `zone`, `name` (`@` for the apex), `type` (A, AAAA, CNAME, MX, TXT, SRV, PTR, NS), `records`, `ttl`. Host names without a trailing dot are relative to the zone, as in zone files: `["10 mail"]`, `["0 100 389 dc01"]`. Authoritative for its name and type; other types at the same name are untouched |

`manager`, `managed_by`, `members`, `groups`, `applies_to` and the gMSA principals accept DNs or sAMAccountNames.

### connection

| Attribute | Default | Notes |
|---|---|---|
| `server` | `DECLARAD_SERVER` | DC host name |
| `domain` | derived from `base_dn` | DNS domain; used for default UPNs, DNS host names and the base DN |
| `base_dn` | from `domain` | |
| `protocol` | `ldaps` | `ldaps` (636), `starttls` or `ldap` (389). Passwords and gMSAs need an encrypted connection |
| `port` | 636 / 389 | |
| `auth` | `simple` | `negotiate` uses Kerberos/NTLM (current ticket when no username is set) |
| `username` / `password` | `DECLARAD_USERNAME` / `DECLARAD_PASSWORD`, then an interactive prompt | |
| `insecure_skip_verify` | `false` | Windows only. On Linux/macOS set `LDAPTLS_REQCERT=never` |
| `ca_cert_dir` | | Linux/macOS: directory of trusted CA certificates |
| `timeout` | `30` | Seconds |

### DNS

```hcl
dns_zone "branch" {
  name           = "branch.corp.local"
  dynamic_update = "secure"
}

dns_record "intranet" {
  zone    = dns_zone.branch.name
  name    = "intranet"
  type    = "A"
  records = ["10.20.0.10", "10.20.0.11"]
  ttl     = 300
}

dns_record "mail" {
  zone    = dns_zone.branch.name
  name    = "@"
  type    = "MX"
  records = ["10 mx1", "20 mx2.corp.local."]
}
```

Zones and records are written directly to the AD DNS partitions over LDAP (`dnsZone`/`dnsNode` objects, MS-DNSP record format). The DNS Server service picks changes up from the directory on its polling interval (about three minutes by default), or immediately after `dnscmd /zonereload`. Zone serials are incremented on every change. `export --type dns_zone,dns_record` skips records clients registered dynamically unless you pass `--include-dynamic-dns`.

## Adopting an existing domain

```sh
declarad init                       # then edit the connection block
declarad export --base "OU=Corp,DC=corp,DC=local" -o corp.dad
declarad plan --adopt               # should show only "take under management"
declarad apply --adopt
```

## Testing

`dotnet test` runs the unit and engine tests against an in-memory AD emulation. The S3 and Azure backend tests run when these environment variables point at real or emulated services:

```sh
docker run -d -p 4566:4566 -e SERVICES=s3 localstack/localstack:4.0
docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0
export DECLARAD_TEST_S3_ENDPOINT=http://127.0.0.1:4566
export DECLARAD_TEST_AZURITE="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
dotnet test
```

## Examples

- `examples/csv-users`: bulk users from an HR CSV export, with one group per department.
- `examples/enterprise`: department modules, typed variables with validation, data sources, a fine-grained password policy, Domain Admins membership, GPO links, a gMSA and a `moved` block.

## Project layout

```
src/Declarad/
  Hcl/         lexer, parser, evaluator, functions, type constraints, formatter
  Config/      configuration loading, modules, references, for_each/count, validation
  Resources/   schema and LDAP mapping for every resource type, data sources
  Directory/   IDirectory, the LDAP implementation, DN/SID utilities, security descriptors
  Engine/      planner (diff + ordering) and applier
  State/       state file and backends (local, s3, azurerm, http) with locking
  Export/      directory → HCL
  Cli/         commands, plan rendering
tests/Declarad.Tests/   unit and engine tests (against an in-memory AD emulation)
```

## Limitations

- Module registry sources (`namespace/name/provider`) are not supported; use git or archive sources.
- GPO contents (settings in SYSVOL) are not managed; GPOs can be linked, but not created or edited.
- Data sources are read during planning, so a data source that looks up an object created in the same apply fails on the first run.
- `insecure_skip_verify` is honoured natively only on Windows.
- Primary-group membership (for example *Domain Users*) is not visible through `memberOf` and can't be managed through `groups`.
