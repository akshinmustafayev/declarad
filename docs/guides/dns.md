# DNS management

declarad manages Active Directory-integrated DNS: zones with [`dns_zone`](../resources/dns_zone.md) and record sets with [`dns_record`](../resources/dns_record.md). They work in zones declarad created and in existing ones.

## How it works

AD-integrated zones live in the directory: a `dnsZone` object per zone and a `dnsNode` object per name, holding binary `dnsRecord` values. declarad reads and writes these objects over the same LDAP connection, so it needs no DNS server API or dynamic-update rights. The DNS Server service on each DC loads directory changes on its polling interval (`DsPollingInterval`, 180 seconds by default). To see a change immediately, run `dnscmd /zonereload <zone>`.

## A branch-site zone

```hcl
dns_zone "branch" {
  name           = "london.corp.local"
  dynamic_update = "secure"
}

locals {
  hosts = {
    files    = "10.20.0.10"
    printers = "10.20.0.11"
    wifi-ctl = "10.20.0.12"
  }
}

dns_record "host" {
  for_each = local.hosts
  zone     = dns_zone.branch.name
  name     = each.key
  type     = "A"
  records  = [each.value]
}

dns_record "files_alias" {
  zone    = dns_zone.branch.name
  name    = "fs"
  type    = "CNAME"
  records = ["files"]
}
```

## Records in an existing zone

```hcl
dns_record "app" {
  zone    = "corp.local"
  name    = "app"
  type    = "A"
  records = ["10.0.5.20", "10.0.5.21"]
  ttl     = 60
}
```

Only the `app` A record set is managed. Everything else in `corp.local`, including records registered dynamically by clients and DCs, is untouched.

## Reverse zones

```hcl
dns_zone "reverse" {
  name = "20.10.in-addr.arpa"
}

dns_record "ptr" {
  for_each = local.hosts
  zone     = dns_zone.reverse.name
  name     = join(".", reverse(slice(split(".", each.value), 2, 4)))   # 10.20.0.10 -> 10.0
  type     = "PTR"
  records  = ["${each.key}.london.corp.local."]
}
```

## Exporting

```sh
declarad export --type dns_zone,dns_record -o dns.dad
```

Dynamically registered records (clients, DHCP) are skipped unless you add `--include-dynamic-dns`. Managing them would make declarad fight their owners.

## Limits

- Supported record types: A, AAAA, CNAME, MX, TXT, SRV, PTR, NS.
- Zone settings beyond SOA, NS and dynamic update (aging, zone transfers, conditional forwarders) are not managed.
- File-backed (non-AD) zones are not supported.
