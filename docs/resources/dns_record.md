# dns_record

A DNS record set: all records of one type at one name in a zone. The zone may be managed by declarad or not.

```hcl
dns_record "intranet" {
  zone    = dns_zone.branch.name
  name    = "intranet"
  type    = "A"
  records = ["10.20.0.10", "10.20.0.11"]
  ttl     = 300
}

dns_record "intranet_v6" {
  zone    = dns_zone.branch.name
  name    = "intranet"
  type    = "AAAA"
  records = ["2001:db8:20::10"]
}

dns_record "www" {
  zone    = dns_zone.branch.name
  name    = "www"
  type    = "CNAME"
  records = ["intranet"]                     # relative: intranet.branch.corp.local.
}

dns_record "mail" {
  zone    = dns_zone.branch.name
  name    = "@"
  type    = "MX"
  records = ["10 mx1", "20 mx2.corp.local."]
}

dns_record "ldap_srv" {
  zone    = "corp.local"                     # a zone declarad doesn't manage
  name    = "_ldap._tcp.branch._sites"
  type    = "SRV"
  records = ["0 100 389 dc01.corp.local."]
}

dns_record "spf" {
  zone    = dns_zone.branch.name
  name    = "@"
  type    = "TXT"
  records = ["v=spf1 mx -all"]
}
```

## Record formats

| Type | Format | Example |
|---|---|---|
| `A` | IPv4 address | `"10.0.0.1"` |
| `AAAA` | IPv6 address | `"2001:db8::1"` |
| `CNAME`, `NS`, `PTR` | host name | `"web"`, `"web.example.org."` |
| `MX` | `preference host` | `"10 mail"` |
| `SRV` | `priority weight port target` | `"0 100 389 dc01"` |
| `TXT` | text (one record per list element; long strings are split into 255-byte chunks) | `"v=spf1 -all"` |

Host names follow zone-file rules: **without a trailing dot they are relative to the zone** (`mail` means `mail.<zone>.`, `@` means the zone itself); with a trailing dot they are absolute. Values are normalized, so equivalent spellings (`2001:0db8::0001`, `MAIL`) don't cause changes.

## Behaviour

- **Authoritative per name and type**: after apply, the name has exactly these records of this type. Records of other types at the same name are untouched.
- The `dnsNode` object is created when needed and deleted when its last record set is removed.
- Records are written as static records (no aging). Each change increments the zone's SOA serial.
- All records of the set share `ttl`; if someone edits the TTL of one of them, the plan rewrites the set.
- Changing `name`, `type` or `zone` removes the old record set and creates the new one.
- A CNAME record set holds exactly one record, and no other `dns_record` may use the same name.
- Apex (`@`) NS records of a zone managed by a `dns_zone` resource belong to its `name_servers`; a `dns_record` for them is rejected.
- Applied after all objects exist, so the zone can be created in the same apply. If the zone doesn't exist at apply time, the step fails with a clear error.

This is an *attachment*: it needs no import. A record set that already exists is taken over, and only real differences are applied.

## Attributes

<!-- schema:dns_record -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `zone` | string |  | required | Zone name, e.g. dns_zone.corp.name. |
| `name` | string |  | required | Name relative to the zone (www, _ldap._tcp), or @ for the zone apex. |
| `type` | string |  | required; one of `A`, `AAAA`, `CNAME`, `MX`, `TXT`, `SRV`, `PTR`, `NS` | A, AAAA, CNAME, MX, TXT, SRV, PTR or NS. |
| `records` | list(string) |  | required | Record data. Host names without a trailing dot are relative to the zone. Authoritative for this name and type. |
| `ttl` | number | `3600` |  | TTL in seconds. |
| `replication_scope` | string | `"domain"` | one of `domain`, `forest`, `legacy` | Where the zone is stored: domain, forest or legacy. |
<!-- /schema -->
