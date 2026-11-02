# Object identity: when declarad creates, changes and deletes objects

**Read this before you apply against a production domain.** It explains why declarad is careful with deletions and what it does in every situation, so you can predict a plan before you run it.

## Why it matters

In Active Directory, an object is not its name. Every object has two permanent identifiers:

- **objectGUID**: assigned at creation, never changes, even when the object is renamed or moved to another OU.
- **objectSid** (for users, groups, computers and service accounts): what Windows security actually uses.

Everything else points at those identifiers, not at the name:

- permissions (ACLs) on file shares, folders, printers, OUs and other objects;
- group memberships and GPO security filtering;
- Exchange mailboxes, user profiles and home folders;
- identities synchronised to Microsoft Entra ID (Entra Connect matches on the objectGUID / `ms-DS-ConsistencyGuid`);
- application and service mappings, delegated rights, certificates mapped to accounts.

If an account is deleted and created again with **the same name**, it gets a **new SID and objectGUID**. To Windows it is a different account: it loses its access everywhere, its mailbox and synced cloud identity are orphaned, and everything that referenced the old one breaks. So declarad treats deleting and re-creating an object as the most expensive thing it can do, and avoids it whenever it can.

## How declarad identifies objects

declarad's [state](concepts.md#state) maps each resource address (`user.alice`) to the object's **objectGUID**. Every plan looks the object up by that GUID, never by its name or DN. Consequences:

- Changing `name` or `path` **renames or moves the same object** (an LDAP ModifyDN). The SID and objectGUID stay.
- Changing attributes **modifies the same object** in place.
- Objects that are not in state are **not managed**. declarad never changes or deletes them, and never takes them over silently.

## What happens in each situation

| Situation | What declarad does |
|---|---|
| New block, nothing at that DN in AD | **Creates** the object |
| New block, but an object already exists at that DN | **Stops with an error.** Take it over explicitly with `declarad import`, an `import` block or `--adopt` |
| Attributes changed | **Updates** the object in place |
| `name` or `path` changed | **Renames / moves** the object in place (same SID and GUID) |
| Block label renamed (`user "alice"` → `user "bob"`), `moved` block, `state mv`, renamed `module` or `for_each` block | **Nothing in AD.** Only the state entry moves to the new address ([details](configuration/moved-and-import.md#renaming-a-block)) |
| Block removed from the configuration | **Deletes** the object, unless protected (see below) |
| `declarad destroy` | **Deletes** everything in state, unless protected |
| Managed object deleted outside declarad (ADUC, scripts), and it is in the AD Recycle Bin | **Restores** it from the Recycle Bin with its SID, attributes and memberships ([details](#objects-deleted-outside-declarad)) |
| Managed object deleted outside declarad, and not restorable | **Creates it again**, and the plan warns that it will be a new object with a new SID |
| Managed object deleted outside declarad, and its block was removed too | **Forgets** it (removes it from state) |
| `declarad state rm user.alice` | Stops managing the object **without deleting it** |

### declarad never "replaces" an AD object

Terraform providers can mark attributes as *ForceNew*: changing them destroys and re-creates the resource. declarad has no such attributes for directory objects:

- Attributes Active Directory doesn't allow to change (a DNS zone's `name`, `replication_scope` and `path`) make the plan **fail** with an explanation. You decide what to do; declarad never deletes and re-creates behind your back.
- Changes AD only allows in steps (for example converting a global group to domain local) are done in place, through the intermediate step AD requires.
- The only `-/+` in a plan is for **attachments** (`group_membership`, `gpo_link`, `dns_record`) whose target changes. That moves a link from one object to another; no object is deleted.

### Renaming blocks never touches AD

The block label is declarad's name for a resource, not the object's name in AD. Renaming it moves the state entry and changes nothing in the directory:

```
  # user.alice has moved to user.bob
    user.bob {
        # dn = "CN=alice,CN=Users,DC=corp,DC=local"
    }

Plan: 0 to add, 0 to change, 0 to destroy.
```

The label is never used as a value: the object's name and logon name are always set explicitly with `name` and `sam_account_name`. Changing those is an in-place rename of the same object.

You can rename a block and change the object in the same edit. A new `path` moves the object to the other OU, and a new `name` renames it, both in place with the same SID and GUID. A renamed block is recognised as the same object when its DN, **its name (CN) or its logon name** matches an object that left the configuration. Only when none matches (which is what replacing one account with another looks like) does the plan show a delete and a create, with a warning. If it really is the same object, add a `moved` block, or rename the block and the object in separate applies.

## Deletion safeguards

- **Only managed objects.** declarad deletes only objects recorded in its state, and only when you remove their block (or run `destroy`).
- **`lifecycle { prevent_destroy = true }`** makes every plan that would delete the resource fail.
- **OUs are protected from accidental deletion by default**, as in ADUC. Deleting one fails until you set `protected_from_accidental_deletion = false` and apply that first.
- **Order.** Deletes run last: attachments are detached first, then objects are deleted deepest DN first. A delete runs early only when it frees a name or logon name that a create or rename in the same plan needs.
- **Attributes you don't manage are left alone.** Removing an attribute from a block clears only the value declarad wrote; values set by others are never touched. Additive lists (`groups`, `group_membership`) only ever remove what declarad added.

## Objects deleted outside declarad

When a managed object has been deleted outside declarad, the next plan first looks for it in the **AD Recycle Bin**:

```
  # user.alice will be restored from the AD Recycle Bin
  # (deleted outside declarad; it is in the AD Recycle Bin and will be restored with its SID, attributes and group memberships)
  + user.alice {
      # dn = "CN=Alice Example,OU=Staff,DC=corp,DC=local"
    }

Plan: 0 to add, 0 to change, 0 to destroy, 1 to restore.
```

- The restore puts the object back at the configured DN, with its SID, objectGUID, attributes, password and group memberships. Differences from the configuration are applied right after it, in the same apply.
- Containers are restored before the objects in them, so a deleted OU with its users comes back in one apply.
- If the object is not in the Recycle Bin (the Recycle Bin is not enabled, or the object was already recycled after the deleted-object lifetime), it is **created again** as a new object with a new SID, and the plan says so.
- If another object now sits at the configured DN, the plan stops instead of guessing. Rename or remove that object, or pass `--no-restore`.
- `--no-restore` on `plan`/`apply` always creates such objects again.
- Restoring needs the **Reanimate Tombstones** extended right on the domain and read access to the *Deleted Objects* container. Domain Admins have both; a delegated service account needs them granted.

The Recycle Bin is a forest-wide optional feature (Windows Server 2008 R2 forest functional level or later). We strongly recommend enabling it: `Enable-ADOptionalFeature 'Recycle Bin Feature' -Scope ForestOrConfigurationSet -Target <forest>`. It can't be disabled again.

## Reading a plan safely

Before typing `yes`, look for these:

| In the plan | Meaning | Check |
|---|---|---|
| `- ... will be destroyed` | An object will be deleted | Did you really remove that block? |
| `+ ... will be created` with a note *new object with a new SID* | A managed object disappeared and will come back as a new identity | Can you restore it from the Recycle Bin or a backup instead? |
| A `-` and a `+` of the same type in one plan, with a warning | A renamed block whose name and logon name both changed, so declarad treats it as a different object | If it is the same object, add a `moved` block |
| `will be taken under management` | An existing object is adopted | Is it the object you meant? |

In CI, run `declarad plan --detailed-exitcode` and require a human review whenever the plan contains destroys.

## FAQ

**I renamed a resource block. Will declarad rename, or delete and re-create, the object in AD?**
Neither. Only the state changes; AD is not touched.

**I changed a user's `name`. Is the account re-created?**
No. The object is renamed in place and keeps its SID and GUID.

**I removed a block. What happens?**
The object is deleted at the next apply (the plan shows it with `-`), unless `prevent_destroy` is set or, for OUs, deletion protection is on. To stop managing an object without deleting it, run `declarad state rm <address>` before removing the block.

**Someone deleted a managed user in ADUC.**
The next plan restores it from the Recycle Bin, with its SID and memberships. Without the Recycle Bin it is created again as a new account, and the plan warns you.

**Can declarad delete objects it doesn't manage?**
No. It only deletes objects recorded in its state.

**An object already exists where I declared a new one.**
The plan fails rather than taking it over. Use `declarad import`, an `import` block, or `--adopt` to take it under management explicitly.
