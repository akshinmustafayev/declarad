# Core concepts

## The workflow

1. **Write** configuration: `.dad` files in HCL describing the objects you want.
2. **Plan**: declarad reads the managed objects from the directory, compares them with the configuration and prints the differences.
3. **Apply**: declarad performs the planned changes and records the result in its state.

Running `plan` again after `apply` should report no changes. When something changes in Active Directory outside declarad (drift), the next plan shows it and apply reverts it.

## Configuration files

All `*.dad` files in the working directory form one configuration (the *root module*). File names and order don't matter. A configuration needs at least one `connection` block saying which directory to manage. See [Syntax](configuration/language.md).

## Resources and addresses

Every resource block declares one object, or several with `for_each`/`count`:

```text
user "jdoe" { ... }                               # address: user.jdoe
user "staff" { for_each = ... }                   # user.staff["jdoe"], user.staff["asmith"]
module "eng" { source = "./modules/dept" }        # module.eng.user.person["jdoe"]
```

The **address** identifies the resource in plans, in state and on the command line (`--target`, `import`, `state rm`).

### Distinguished names are computed

Every object resource has a `name` (its RDN value) and a `path` (the parent DN). Its DN is `<RDN type>=<name>,<path>`, so `ou.staff.dn` is known when the configuration is read and other resources can reference it without an apply.

## State

declarad keeps a **state** file (`declarad.state.json` by default, or a [remote backend](configuration/backends.md)). It maps each address to:

- the object's **objectGUID**, AD's permanent identifier. Objects are found by GUID, so renames and moves are tracked and never mistaken for "delete + create";
- the **connection** the object lives in;
- the last applied values of attributes declarad manages (used to know what to clear when you remove an attribute from the configuration);
- `prevent_destroy`.

State holds no passwords. It does contain DNs and attribute values, so treat it as internal data.

Objects in the directory that are not in state are **not managed**: declarad never deletes them. [Object identity](object-identity.md) explains when declarad creates, changes and deletes objects, and why. To take an existing object under management, [import or adopt it](configuration/moved-and-import.md).

## How a plan is built

For every resource in the configuration:

- **In state** → read the object by GUID and diff. If the object vanished, it is restored from the AD Recycle Bin when possible, otherwise created again (see [Object identity](object-identity.md)).
- **Not in state** → an `import` block or a `moved` block may connect it to an existing object; a renamed block label is detected automatically and keeps the object and its label-derived values (see [renaming a block](configuration/moved-and-import.md#renaming-a-block)); otherwise, if an object already exists at the DN, the plan fails and tells you to import it (or to run with `--adopt`).

Every address in state that is no longer in the configuration is planned for deletion (unless `prevent_destroy` or OU protection forbids it).

## How apply runs

Apply works in three phases so that references between objects never create ordering problems:

0. **Name-freeing deletes**: a delete whose DN, sAMAccountName or UPN is needed by an object being created or renamed runs first (for example renaming `b` to the name of `a` while removing `a`).
1. **Objects**: create, rename/move and update objects in dependency order. Parents (OUs) come before what is placed in them; references (`path = ou.x.dn`) and `depends_on` add more edges.
2. **Deferred attributes and attachments**: memberships (`members`, `groups`), manager links, `applies_to`, gMSA principals, and attachment resources (`group_membership`, `gpo_link`, `dns_record`). By now every object exists, so two groups can contain each other.
3. **Deletes**: attachments are detached first, then objects are deleted deepest DN first (users before their OU).

State is saved after **every** step. If a step fails, the run stops, the state reflects exactly what happened, and the next plan picks up from there.

## Who owns an attribute

declarad only touches what you declare:

| Situation | Behaviour |
|---|---|
| Attribute set in configuration (or by a default) | Managed: drift is reverted. |
| Attribute never set | Not managed: helpdesk edits are left alone. |
| Attribute removed from configuration | Cleared once (declarad clears the value it wrote), then no longer managed. |
| `lifecycle { ignore_changes = [x] }` | Set at creation, then never compared. |
| Creation-only attributes (`initial_password`, `change_password_at_logon`, `password_interval_days`) | Applied when the object is created, never compared. |

### Lists

- **Authoritative lists** (`group.members`, `password_policy.applies_to`, SPNs, `proxy_addresses`, gMSA principals, `dns_record.records`) contain exactly what you list. Omit the attribute to leave the list unmanaged.
- **Additive lists** (`groups` on users, computers, contacts, service accounts and groups, and `group_membership.members`) only add. State records which memberships declarad itself added; only those are ever removed (when you remove them from the list, or remove the resource). Memberships that already existed are never touched, and renames in the same apply are followed.
- When an object lists a group in `groups`, or a `group_membership` targets a group whose `members` is managed, those members are merged into the group's member list, so the declarations never fight.
- Two resources can never manage the same object: adopting an object already managed under another address is refused.

## Safety rules

- Existing objects are never adopted silently: `import`, an `import` block or `--adopt` is required.
- `lifecycle { prevent_destroy = true }` makes any plan that would delete the resource fail.
- OUs are created **protected from accidental deletion** (as in ADUC). Removing a protected OU from the configuration fails the plan until you set `protected_from_accidental_deletion = false` and apply.
- Immutable attributes (e.g. a DNS zone's `replication_scope`) fail the plan instead of being changed.
- `apply` and `destroy` ask for confirmation unless `--auto-approve`.
- A new user whose password the domain rejects is deleted again instead of being left half-configured.
- State is locked during changes; a second run waits or fails.

## Unknown values

`declarad validate` and `graph` work offline. Values that need the directory (data sources) are *unknown* there. Unknowns only spread where they matter: `length()` of a list with unknown elements is known, `keys()` and `lookup()` of known keys work, and `unknown || true` is `true`. Validation skips checks that depend on unknown values and warns when a `for_each` can't be expanded. `plan` and `apply` always connect, so everything is known.

## Connections

A configuration can manage several directories. Each object belongs to exactly one [connection](configuration/connections.md); its connection is recorded in state, and objects cannot move between connections.

## Objects deleted outside declarad

When a managed object has been deleted outside declarad, the next plan restores it from the AD Recycle Bin, keeping its SID and memberships, and creates it again only when that isn't possible. See [Object identity](object-identity.md#objects-deleted-outside-declarad).
