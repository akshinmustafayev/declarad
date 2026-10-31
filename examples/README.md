# Examples

Each directory is a complete configuration. All of them pass `declarad validate` and `declarad fmt --check` (checked by the test suite). Point the `connection` block at your domain before planning.

| Example | Shows |
|---|---|
| [basic](basic) | The smallest useful configuration: an OU, a group, a user, an output. |
| [csv-users](csv-users) | Users from a CSV file with `for_each`, one group per department via `for` grouping (`...`). |
| [hr-sync](hr-sync) | HR-driven lifecycle: per-office OUs, managers, leavers disabled and moved, preconditions validating the data. |
| [enterprise](enterprise) | Department modules with typed variables and validation, data sources, a fine-grained password policy, Domain Admins membership, a GPO link, a gMSA and a `moved` block. |
| [tiering](tiering) | A tier 0/1/2 admin model as a module: admin OUs, accounts, groups, password policies, enforced GPO links, `prevent_destroy`. |
| [servers](servers) | Pre-staged computers with `for_each` and `count`, SPNs, a web-farm group and a gMSA it may use. |
| [dns](dns) | A branch DNS zone and reverse zone, A/PTR pairs, CNAME, MX, TXT and SRV records, records in an existing zone. |
| [multi-site](multi-site) | Two domains through two connections, a per-site module (OU + DNS zone) mapped with `connections`, S3 state. |
| [import-existing](import-existing) | `import` and `moved` blocks, data sources (including an LDAP search), `group_membership` on a built-in group. |
| [backends/s3](backends/s3), [backends/azure](backends/azure), [backends/gitlab](backends/gitlab) | Shared state with locking in S3, Azure Blob Storage and GitLab (HTTP backend). |
| [git-modules](git-modules) | Modules pinned to git tags and an archive module (`declarad init` downloads them). |

Variables without defaults are passed with `--var name=value` or `DECLARAD_VAR_<name>`, e.g. `DECLARAD_VAR_initial_password` for the user examples.
