# dns_zone

An Active Directory-integrated DNS zone.

```hcl
dns_zone "branch" {
  name              = "branch.corp.local"
  replication_scope = "domain"       # DomainDnsZones: all DNS servers in the domain
  dynamic_update    = "secure"
  admin_email       = "dns-admins@corp.local"
  name_servers      = ["dc01.corp.local.", "dc02.corp.local."]
}

dns_zone "reverse_10" {
  name = "10.in-addr.arpa"
}
```

## Storage

| `replication_scope` | Container | Replicated to |
|---|---|---|
| `domain` (default) | `CN=MicrosoftDNS,DC=DomainDnsZones,<domain>` | DNS servers (DCs) of the domain |
| `forest` | `CN=MicrosoftDNS,DC=ForestDnsZones,<forest root>` | DNS servers of the forest (set `forest_base_dn` on the connection when the forest root differs from the domain) |
| `legacy` | `CN=MicrosoftDNS,CN=System,<domain>` | All DCs of the domain (Windows 2000-compatible) |

The scope and the name can't be changed after creation.

## Behaviour

- Creating a zone writes the `dnsZone` object (zone type and dynamic update properties) and its apex `@` node with the SOA record and NS records. The DNS Server service loads it on its next directory poll (about three minutes by default), or immediately after `dnscmd /zonereload <zone>` / restarting the service.
- SOA settings and `name_servers` are managed at the apex; other records at `@` (MX, TXT, ...) belong to [`dns_record`](dns_record.md) resources and are left alone.
- Every change increments the SOA serial.
- **Deleting a zone deletes every record in it.** Protect important zones with `lifecycle { prevent_destroy = true }`.
- `primary_server` defaults to the connection's server.

## Attributes

<!-- schema:dns_zone -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required; immutable | Zone name, e.g. corp.example or 10.in-addr.arpa. |
| `replication_scope` | string | `"domain"` | immutable; one of `domain`, `forest`, `legacy` | domain (DomainDnsZones), forest (ForestDnsZones) or legacy (domain partition). |
| `path` | string | computed | immutable; DN | Container of the zone; derived from replication_scope. |
| `dynamic_update` | string | `"secure"` | one of `none`, `secure`, `nonsecure_and_secure` | none, secure or nonsecure_and_secure. |
| `primary_server` | string | computed | required | SOA primary name server. Defaults to the connection's server. |
| `admin_email` | string | computed |  | SOA responsible person, as an address (hostmaster@corp.example) or DNS name. Defaults to hostmaster.<zone>. |
| `refresh` | number | `900` |  | SOA refresh interval in seconds. |
| `retry` | number | `600` |  | SOA retry interval in seconds. |
| `expire` | number | `86400` |  | SOA expire time in seconds. |
| `minimum_ttl` | number | `3600` |  | SOA minimum (negative caching) TTL in seconds. |
| `ttl` | number | `3600` |  | TTL of the SOA and NS records in seconds. |
| `name_servers` | list(string) | computed |  | NS records at the zone apex. Defaults to [primary_server]. |
<!-- /schema -->

Also exposes `dn`.
