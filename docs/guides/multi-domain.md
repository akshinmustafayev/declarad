# Managing several domains

One configuration can manage several domains or forests, each through its own [connection](../configuration/connections.md). Typical uses: regional child domains, a resource forest, or production and staging domains with the same layout.

```hcl
connection {
  server = "dc01.hq.corp.local"
  domain = "hq.corp.local"
}

connection "eu" {
  server   = "dc01.eu.corp.local"
  domain   = "eu.corp.local"
  password = env("DECLARAD_EU_PASSWORD")
}

# The same module, instantiated per domain.
module "baseline_hq" {
  source = "./modules/baseline"
}

module "baseline_eu" {
  source      = "./modules/baseline"
  connections = { default = connection.eu }
}

# Cross-domain: put an HQ group into an EU domain-local group by DN.
group "eu_file_access" {
  name       = "eu_file_access"
  connection = connection.eu
  scope      = "domain_local"
  members    = [group.hq_engineers.dn]
}
```

Notes:

- Each connection gets its own credentials; use `env()` per connection.
- Names are unique per connection, so both domains can have `OU=Staff`.
- Membership by sAMAccountName resolves in the owner's domain. For members from another domain, use their DN (via a reference or a data source with `connection`).
- Each object is recorded with its connection in state. Moving a resource to another connection is refused: objects can't move between directories.
- One state file covers all connections. If teams own different domains, use separate configurations (and backends) instead.

See `examples/multi-site`.
