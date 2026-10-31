# Troubleshooting

Run any command with `-v` for debug logs (on stderr).

## Connecting

| Error | Cause and fix |
|---|---|
| `Cannot reach LDAP server dc01:636 (Ldaps)` | Wrong name or port, a firewall, or (most often) the server certificate isn't trusted by this machine. Test with `openssl s_client -connect dc01:636`. On Linux/macOS, trust the CA in the system store or set `ca_cert_dir`; for a quick test only, `LDAPTLS_REQCERT=never`. |
| `Authentication failed ... invalid credentials` | Wrong username/password. Use the UPN form `user@domain`. |
| `Could not determine the domain naming context` | Set `base_dn` in the connection block. |

## Planning

| Error | Fix |
|---|---|
| `... already exists in the directory but is not managed by declarad` | Import it (`import` block or `declarad import`) or use `--adopt`. |
| `... cannot be deleted: it is protected from accidental deletion` | Set `protected_from_accidental_deletion = false`, apply, then remove the OU. |
| `... has lifecycle.prevent_destroy = true` | Remove the setting and apply first if the deletion is intended. |
| `... refers to 'x', which is neither a DN nor a sAMAccountName found` | A member/manager name doesn't exist. Check spelling or create the object. |
| `Dependency cycle between ...` | Two objects reference each other in non-deferred attributes (e.g. paths). Memberships and managers never cause cycles. |
| `... cannot be changed after creation` | An immutable attribute (e.g. DNS zone scope) changed. Create a new resource instead. |
| `... moved from connection 'a' to 'b'` | Objects can't move between directories: `state rm` the resource and import it in the new directory. |
| `Module 'x' ... is not installed` / `source ... changed` | Run `declarad init`. |
| `The backend configuration changed since 'declarad init'` | Run `declarad init --migrate-state` (or `--reconfigure`). |

## Applying

| Error | Fix |
|---|---|
| `... password does not meet the domain password policy` | Choose a stronger `initial_password`. The half-created account was removed again. |
| `Setting passwords requires an encrypted (LDAPS/StartTLS) connection` | Use `protocol = "ldaps"` or `"starttls"`. |
| `insufficient access rights` | The service account lacks delegated rights on that OU or object class. |
| `not allowed on non-leaf` when deleting an OU | It still contains objects declarad doesn't manage. Move or delete them first. |
| gMSA creation fails | Create a KDS root key (`Add-KdsRootKey`) and wait for replication. |

A failed apply has already saved state for every completed step. Fix the cause and run `apply` again; it continues from where it stopped.

## State

| Situation | Fix |
|---|---|
| `The state at ... is locked by another operation` | Wait (`--lock-timeout 5m`), or if that run died, `declarad force-unlock <ID>`. |
| `was changed by another run` | Someone applied meanwhile. Run the command again. |
| State lost or corrupt | Local backend: `declarad.state.json.backup`. S3/Azure: object versioning. Last resort: `export` and adopt again. |

## DNS changes not visible

The DNS Server service loads directory changes every three minutes by default. Run `dnscmd /zonereload <zone>`, or check the object with `Get-DnsServerResourceRecord`.
