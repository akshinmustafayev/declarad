# declarad documentation

declarad manages Active Directory the way Terraform manages cloud infrastructure: you describe users, groups, OUs, computers, service accounts, password policies, GPO links and DNS in configuration files, review a **plan** of the differences, and **apply** it.

## Start here

- [Getting started](getting-started.md): install, first configuration, first apply.
- [Core concepts](concepts.md): state, planning, apply phases, who owns which attribute, safety rules.
- [Command reference](cli.md): every command, option and exit code.

## Configuration language

- [Syntax and expressions](configuration/language.md): blocks, attributes, types, operators, templates, `for`, splat.
- [Functions](configuration/functions.md): the built-in function library.
- [Variables, locals and outputs](configuration/variables-locals-outputs.md)
- [Resource meta-arguments](configuration/meta-arguments.md): `for_each`, `count`, `depends_on`, `connection`, `lifecycle`.
- [Connections](configuration/connections.md): one or several directories.
- [Modules](configuration/modules.md): local, git and archive sources.
- [Data sources](configuration/data-sources.md): read existing objects.
- [moved and import blocks](configuration/moved-and-import.md): refactoring and adoption.
- [State and backends](configuration/backends.md): local, S3, Azure Blob, HTTP; locking and migration.

## Resource reference

| Resource | Manages |
|---|---|
| [`ou`](resources/ou.md) | Organizational units |
| [`container`](resources/container.md) | Generic containers |
| [`group`](resources/group.md) | Security and distribution groups |
| [`user`](resources/user.md) | User accounts |
| [`computer`](resources/computer.md) | Pre-staged computer accounts |
| [`contact`](resources/contact.md) | Mail contacts |
| [`service_account`](resources/service_account.md) | Group managed service accounts (gMSA) |
| [`password_policy`](resources/password_policy.md) | Fine-grained password policies |
| [`group_membership`](resources/group_membership.md) | Additive members of groups you don't otherwise manage |
| [`gpo_link`](resources/gpo_link.md) | Links of existing GPOs to OUs or the domain |
| [`dns_zone`](resources/dns_zone.md) | AD-integrated DNS zones |
| [`dns_record`](resources/dns_record.md) | DNS record sets |

Data sources are described in [configuration/data-sources.md](configuration/data-sources.md).

## Guides

- [Adopting an existing domain](guides/adopting-existing-domain.md)
- [Bulk users from HR data](guides/bulk-users-from-csv.md)
- [Running declarad in CI/CD](guides/ci-cd.md)
- [Managing several domains](guides/multi-domain.md)
- [DNS management](guides/dns.md)
- [Troubleshooting](guides/troubleshooting.md)

## For contributors

- [Architecture](architecture.md): how the code is organized, how a run flows through it, and how to add a resource type.
- [Examples](../examples/README.md): runnable sample configurations.
