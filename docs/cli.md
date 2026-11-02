# Command reference

```
declarad [-chdir=DIR] <command> [options]
```

## -chdir

```sh
declarad -chdir=environments/prod plan
```

As in Terraform, `-chdir=DIR` goes **before** the subcommand and switches the working directory before anything else runs. The configuration, `.declarad/`, the default state file and every relative path given to other options (`--state`, `fmt` paths, `export -o`, `state push`, `--backend-config` files, `file()` in the configuration) are then found in `DIR`. `-chdir DIR` and the short alias `-C DIR` are accepted in the same position. Placing `-chdir` after the subcommand is an error.

## Options

Accepted by every command.

| Option | Description |
|---|---|
| `--var name=value` | Set a root variable. Repeatable. Values for `string`/`number`/`bool` variables are taken as text and converted; collections are parsed as HCL: `--var 'admins=["a","b"]'`. Naming an undeclared variable is an error. |
| `--backend-config key=value` / `--backend-config file.hcl` | Override [backend](configuration/backends.md) settings. Repeatable. Values given to `init` are remembered for later commands. |
| `--state <file>` | Use a local state file instead of the configured backend. |
| `--lock=false` | Don't lock the state. Use only when you know nothing else runs. |
| `--lock-timeout 30s` | Wait this long for a held lock (`s`, `m`, `h`; default `0s`). |
| `-v` | Debug logging (to stderr). |
| `--no-color` | Plain output (also when `NO_COLOR` is set or output is redirected). |

## Environment variables

| Variable | Used for |
|---|---|
| `DECLARAD_PASSWORD`, `DECLARAD_USERNAME`, `DECLARAD_SERVER` | Defaults for the connection block. |
| `DECLARAD_VAR_<name>` | Value of root variable `<name>`. |
| `LDAPTLS_REQCERT=never` | Linux/macOS: skip LDAPS certificate validation (testing only). |
| `AWS_*`, `ARM_ACCESS_KEY`, `ARM_SAS_TOKEN`, `ARM_CONNECTION_STRING`, `TF_HTTP_*` | Backend credentials. |
| `NO_COLOR` | Disable colors. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (for `plan --detailed-exitcode`: no changes). |
| 1 | Error, or the user declined the apply. |
| 2 | `plan --detailed-exitcode` only: changes are pending. |
| 3 | `fmt --check`: files need formatting. |

## init

```
declarad init [--migrate-state] [--reconfigure] [--force-copy] [--upgrade]
```

Prepares the working directory:

1. If it contains no `.dad` files, writes a starter `main.dad`.
2. Checks `required_version`.
3. Downloads remote [modules](configuration/modules.md) into `.declarad/modules` (`--upgrade` fetches them again).
4. Configures the [state backend](configuration/backends.md) and records it in `.declarad/backend.json`, including `--backend-config` values. When the backend changed and the old one has state, `--migrate-state` copies it (`--force-copy` overwrites existing state at the destination; a local source file is renamed to `<path>.migrated`) and `--reconfigure` starts without it.

Run `init` after cloning a configuration, after changing the backend block, and after adding or changing remote module sources.

## validate

Parses and evaluates the configuration offline and reports every error with its file, line and column. Data sources are treated as unknown.

## fmt

```
declarad fmt [paths...] [--check] [--diff] [-r]
```

Rewrites files in canonical form: two-space indentation, one space around `=`, aligned `=` in runs of single-line attributes, single blank lines. Heredocs, comments and strings are never changed, and declarad refuses to write output whose tokens differ from the input. `--check` only reports (exit 3 if anything would change); `--diff` shows the changes; `-r` includes subdirectories such as local modules.

## plan

```
declarad plan [--adopt] [--destroy] [--target addr]... [--detailed-exitcode] [--no-restore]
```

Shows the changes `apply` would make. Symbols: `+` create (or restore from the AD Recycle Bin), `~` update in place, `-` destroy, `-/+` replace (attachments only). Notes explain moved addresses, imports, adoption and objects deleted outside declarad. Outputs are shown at the end.

- `--adopt`: take existing objects that sit exactly where a new resource would be created under management instead of failing.
- `--destroy`: plan the deletion of everything in state.
- `--target`: limit the plan to these addresses and everything they depend on (for `--destroy`: everything that depends on them). Use for emergencies only; follow with a full plan.
- `--detailed-exitcode`: exit 2 when there are changes, for drift detection in CI.
- `--no-restore`: create managed objects that were deleted outside declarad again, instead of restoring them from the AD Recycle Bin (see [Object identity](object-identity.md#objects-deleted-outside-declarad)).

## apply

```
declarad apply [--auto-approve] [--adopt] [--target addr]... [--no-restore]
```

Plans, shows the plan, asks for `yes` (unless `--auto-approve`), applies, and prints outputs. In a non-interactive shell `--auto-approve` is required. If a step fails, state is saved and the error is printed; fix the cause and run `apply` again.

## destroy

```
declarad destroy [--auto-approve] [--target addr]...
```

Deletes everything this configuration manages. Protected OUs and `prevent_destroy` resources make it fail until you lift the protection.

## import

```
declarad import <address> <dn-or-objectGUID>
```

Records an existing object in state under an address that is declared in the configuration, without changing it. The next plan shows how the object differs from the configuration. Prefer an [`import` block](configuration/moved-and-import.md) when the import should be reviewed in a plan.

## export

```
declarad export [--base DN] [--type t1,t2] [--connection name] [-o file] [--include-dynamic-dns] [--force]
```

Generates configuration from existing objects: references between exported objects (`path = ou.sales.dn`, `members = [user.jdoe.dn]`) instead of raw DNs, and values equal to defaults omitted. Default types: `ou`, `group`, `user`, `computer`, `contact`, `service_account`, `password_policy`, `gpo_link`. Add `container`, `dns_zone`, `dns_record` explicitly. Follow with `plan --adopt`.

## output

```
declarad output [name] [--json]
```

Prints outputs recorded by the last apply. A single string output prints raw (handy in scripts). Sensitive outputs are not stored.

## graph

Prints the dependency graph in Graphviz DOT: `declarad graph | dot -Tsvg > graph.svg`.

## schema

```
declarad schema [type] [--markdown]
```

Without arguments: lists resource types, data sources, backends and functions. With a type or data source: its attributes, types, defaults and flags.

## force-unlock

```
declarad force-unlock <lock-id> [--force]
```

Removes a lock left behind by a crashed run. The lock ID is printed in the "state is locked" error. Make sure no other run is active.

## state

| Command | Description |
|---|---|
| `state list` | Managed addresses and their DNs. |
| `state show <address>` | GUID, DN, connection and recorded attributes. |
| `state rm <address>...` | Stop managing objects (they stay in the directory). |
| `state mv <from> <to>` | Rename an address. Prefer [`moved` blocks](configuration/moved-and-import.md) in shared configurations. |
| `state pull` | Print the state JSON. |
| `state push <file\|->` | Replace the state. Refused if the lineage differs or the backend has a newer serial, unless `--force`. The pushed state gets a serial newer than the current one. |
