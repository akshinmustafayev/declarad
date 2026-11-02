# <img src="assets/icon.svg" height="42" width="42"/> declarad

<a href="https://github.com/akshinmustafayev/declarad/blob/master/LICENSE">
  <img src="https://img.shields.io/github/license/akshinmustafayev/declarad" alt="License" />
</a>
<a href="https://github.com/akshinmustafayev/declarad/releases">
  <img src="https://img.shields.io/github/v/release/akshinmustafayev/declarad?include_prereleases" alt="Latest release" />
</a>
<a href="https://github.com/akshinmustafayev/declarad/releases">
  <img src="https://img.shields.io/github/downloads/akshinmustafayev/declarad/total" alt="Downloads" />
</a>
<a href="https://dotnet.microsoft.com/download">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
</a>
<img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue" alt="Platforms" />
<a href="https://github.com/akshinmustafayev/declarad/stargazers">
  <img src="https://img.shields.io/github/stars/akshinmustafayev/declarad?style=social" alt="GitHub stars" />
</a>
<a href="https://github.com/akshinmustafayev/declarad/graphs/contributors">
  <img src="https://img.shields.io/github/contributors/akshinmustafayev/declarad" alt="Contributors" />
</a>


## :newspaper: Description

**declarad** is infrastructure-as-code for **Active Directory**. Describe your users, groups, OUs, computers, service accounts, password policies, GPO links and DNS in simple text files. Review a **plan** of what will change, then **apply** it. If you know Terraform, you already know how declarad works.

- :memo: **Your directory in git**: every change is a reviewable diff, with history.
- :mag: **Plan before you touch anything**: see exactly what will be created, changed or deleted.
- :repeat: **Repeatable**: the same files produce the same directory, in the lab and in production.
- :penguin: **Runs anywhere**: Windows, Linux and macOS. It talks LDAP/LDAPS straight to your domain controllers, so you don't need RSAT, PowerShell or WinRM.

> [!IMPORTANT]
> **Before you point declarad at a production domain, please read [How declarad protects your objects](#shield-how-declarad-protects-your-objects)** (and the full [Object identity](docs/object-identity.md) guide). Deleting and re-creating an AD object gives it a new SID, which breaks permissions, memberships, mailboxes and synced identities. declarad is built to avoid that, and it helps to know exactly when it creates, changes or deletes objects.

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
  title            = "Lead"
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


## :sparkles: Features

| | |
|---|---|
| :busts_in_silhouette: **Resources** | OUs, containers, groups, users, computers, contacts, gMSAs, fine-grained password policies, group memberships, GPO links, DNS zones and records |
| :scroll: **Full HCL language** | Variables, locals, outputs, `for_each`, `count`, `for` expressions, templates, and the Terraform function library |
| :package: **Modules** | Local folders, git repositories (HTTPS or SSH) and zip/tar archives |
| :cloud: **Shared state with locking** | Local file, Amazon S3 (and S3-compatible storage), Azure Blob Storage, and HTTP (e.g. GitLab-managed state) |
| :globe_with_meridians: **Several domains** | One configuration can manage several domains or forests through named connections |
| :inbox_tray: **Adopt what you have** | `export` turns existing objects into configuration; `import` and `--adopt` bring them under management |
| :shield: **Safe by default** | Objects are tracked by objectGUID, so renames never re-create them; deleted objects are restored from the AD Recycle Bin; existing objects are never silently taken over; OUs are deletion-protected. [How it works](#shield-how-declarad-protects-your-objects) |
| :robot: **CI-friendly** | `plan --detailed-exitcode` for drift detection, `fmt --check`, `validate`, and non-interactive `apply --auto-approve` |


## :shield: How declarad protects your objects

In Active Directory an object is not its name: it is its **objectGUID** and **SID**. Permissions, group memberships, mailboxes, profiles and Entra ID sync all point at those. An account deleted and created again with the same name is, to Windows, a *different* account. So declarad treats "delete and re-create" as the most expensive thing it can do:

- **Objects are tracked by objectGUID.** Changing `name` or `path` renames or moves the same object; it is never re-created.
- **Renaming a block changes nothing in AD.** `user "alice"` → `user "bob"` only moves the entry in declarad's state, and the account keeps its logon name.
- **There is no "replace" for AD objects.** If AD doesn't allow a change, the plan fails and explains why, instead of deleting and re-creating.
- **declarad only deletes what it manages**, and only when you remove the block or run `destroy`. `prevent_destroy` and the OU deletion protection stop even that.
- **Existing objects are never taken over silently.** Use `import`, an `import` block or `--adopt`.
- **Deleted outside declarad?** The next plan restores the object from the **AD Recycle Bin** with its SID and memberships, instead of creating a new one.

| You do this | declarad does this in AD |
|---|---|
| Change attributes | Updates the same object |
| Change `name` / `path` | Renames / moves the same object |
| Rename the block, use `moved`, rename a module | Nothing (state only) |
| Remove the block | Deletes the object (unless protected) |
| Someone deletes the object in ADUC | Restores it from the Recycle Bin |

Always read the plan before typing `yes`, especially lines with `-` (destroy). The [Object identity](docs/object-identity.md) guide covers every case, the safeguards and an FAQ.


## :rocket: Quick start

**1. Get declarad.** Download the zip for your system from [Releases](https://github.com/akshinmustafayev/declarad/releases) and put `declarad` (or `declarad.exe`) on your `PATH`.

- `*-portable.zip` runs on its own, with nothing to install.
- The smaller zip without `-portable` needs the [.NET 10 runtime](https://dotnet.microsoft.com/download).

You can also [build it yourself](#hammer_and_wrench-building-from-source).

**2. Create a configuration.**

```sh
mkdir corp-ad && cd corp-ad
declarad init                      # creates a starter main.dad
```

Edit the `connection` block in `main.dad` to point at your domain controller. Keep the password out of the file: declarad reads `DECLARAD_PASSWORD`, or asks for it.

**3. Plan and apply.**

```sh
declarad plan                      # what would change?
declarad apply                     # make it so (asks for "yes" first)
```

**Already have a domain full of objects?** Generate configuration from it and take it under management:

```sh
declarad export --base "OU=Corp,DC=corp,DC=local" -o corp.dad
declarad plan --adopt              # should only show "take under management"
declarad apply --adopt
```

More: the [getting started guide](docs/getting-started.md) and the [examples](examples/README.md).


## :computer: Commands

| Command | What it does |
|---|---|
| `declarad init` | Install modules, set up the state backend, create a starter `main.dad` |
| `declarad validate` | Check the configuration without contacting the directory |
| `declarad fmt` | Format `.dad` files (`--check` for CI) |
| `declarad plan` | Show what would change |
| `declarad apply` | Plan, confirm, then apply |
| `declarad destroy` | Delete everything this configuration manages |
| `declarad import <address> <dn-or-guid>` | Start managing an existing object |
| `declarad export` | Generate configuration from existing objects |
| `declarad output` | Show output values |
| `declarad graph` | Dependency graph in Graphviz DOT format |
| `declarad schema [type]` | Every resource type and attribute, built in |
| `declarad state list/show/rm/mv/pull/push` | Inspect or repair the state |
| `declarad force-unlock <id>` | Release a lock left by a crashed run |

Handy options: `-chdir=DIR` (before the command, as in Terraform), `--var name=value`, `--target <address>`, `--no-color`, `-v` for debug logging. The [command reference](docs/cli.md) has all of them.


## :hammer_and_wrench: Building from source

Anyone can build declarad. All you need is git and the free [.NET 10 SDK](https://dotnet.microsoft.com/download), on Windows, Linux or macOS.

**Check the SDK:**

```sh
dotnet --version                   # should print 10.x
```

**Clone, build and test:**

```sh
git clone https://github.com/akshinmustafayev/declarad.git
cd declarad
dotnet build
dotnet test                        # runs against a built-in AD emulation; no domain controller needed
```

**Run it straight from the source:**

```sh
dotnet run --project src/Declarad -- --help
```

**Make your own executable:**

```sh
dotnet publish src/Declarad -c Release -o out
./out/declarad --version           # out\declarad.exe on Windows
```

Copy the `out` folder anywhere and add it to your `PATH`. This build needs the .NET 10 runtime on the machine. For a single file that runs without .NET installed, use the release scripts below.

**Build release zips for every platform:**

```sh
build/build.sh                     # macOS, Linux, Git Bash, WSL
```

```powershell
.\build\build.ps1                  # Windows PowerShell or pwsh
```

This cross-compiles Windows, Linux and macOS, each for x64 and arm64, in a portable (self-contained) and a framework-dependent variant. It writes the zips and a `SHA256SUMS` file to `build/dist/`. To build only what you need, run for example `build/build.sh --rids "linux-x64" --variants portable`. The [build README](build/README.md) has all the options.

> [!NOTE]
> On Linux and macOS declarad uses the system OpenLDAP library. It is already there on macOS and most Linux distributions; on minimal images install it with `apt install libldap2` or `dnf install openldap`.


## :test_tube: Testing

`dotnet test` runs more than 260 unit and engine tests against an in-memory Active Directory emulation. The S3 and Azure backend tests also run when you point them at local emulators:

```sh
docker run -d -p 4566:4566 -e SERVICES=s3 localstack/localstack:4.0
docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0
export DECLARAD_TEST_S3_ENDPOINT=http://127.0.0.1:4566
export DECLARAD_TEST_AZURITE="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
dotnet test
```


## :books: Documentation

| | |
|---|---|
| :checkered_flag: [Getting started](docs/getting-started.md) | Install, first configuration, first apply |
| :brain: [Core concepts](docs/concepts.md) | State, planning, apply phases, attribute ownership, safety rules |
| :keyboard: [Command reference](docs/cli.md) | Every command, option and exit code |
| :pencil2: [Configuration language](docs/configuration/language.md) | Syntax, expressions, [functions](docs/configuration/functions.md), [variables](docs/configuration/variables-locals-outputs.md), [modules](docs/configuration/modules.md) |
| :card_file_box: [State and backends](docs/configuration/backends.md) | Local, S3, Azure Blob, HTTP; locking and migration |
| :jigsaw: [Resource reference](docs/README.md#resource-reference) | Every resource type and its attributes |
| :compass: Guides | [Adopting a domain](docs/guides/adopting-existing-domain.md), [bulk users from CSV](docs/guides/bulk-users-from-csv.md), [CI/CD](docs/guides/ci-cd.md), [several domains](docs/guides/multi-domain.md), [DNS](docs/guides/dns.md), [troubleshooting](docs/guides/troubleshooting.md) |
| :bulb: [Examples](examples/README.md) | Ready-to-run configurations, from a single user to a multi-site enterprise layout |
| :building_construction: [Architecture](docs/architecture.md) | How the code is organized, for contributors |


## :warning: Limitations

- GPOs can be linked, but their contents (the settings in SYSVOL) are not created or edited.
- Module registry sources (`namespace/name/provider`) are not supported; use git or archive sources.
- A data source that looks up an object created in the same apply fails on the first run.
- Primary-group membership (for example *Domain Users*) can't be managed through `groups`.

The [roadmap](ROADMAP.md) shows what's done and what's next.


## :handshake: Contributing

Issues and pull requests are welcome! Before you open a pull request:

1. `dotnet build` finishes with no warnings.
2. `dotnet test` passes, and new behaviour comes with a test.
3. `dotnet run --project src/Declarad -- fmt --check --recursive examples` passes if you touched the examples.

Read the [architecture notes](docs/architecture.md) first; they also explain how to add a new resource type.


## :page_facing_up: License

A license has not been chosen yet.
