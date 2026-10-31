# Adopting an existing domain

Most domains already contain years of objects. declarad never touches objects it doesn't manage, so you can adopt gradually: one OU, one team, one object type at a time.

## 1. Start with a scope

Pick something self-contained, for example one OU tree:

```sh
mkdir corp-ad && cd corp-ad
declarad init                     # edit the connection block in main.dad
```

## 2. Export it

```sh
declarad export --base "OU=Sales,DC=corp,DC=local" -o sales.dad
declarad export --base "OU=Sales,DC=corp,DC=local" --type gpo_link -o sales-gpo-links.dad
```

The export:
- writes references between exported objects (`path = ou.sales.dn`, `members = [user.jdoe.dn]`) and keeps raw DNs for everything else;
- omits values equal to declarad's defaults;
- skips AD's critical system objects.

Users' `initial_password` can't be exported. Existing accounts don't need it.

## 3. Review and tidy

The export is a starting point. Typical edits:
- rename labels to something meaningful (`user.john_doe` → `user.jdoe`);
- replace repetition with `for_each` over a variable or CSV file;
- add `lifecycle { ignore_changes = [...] }` for attributes another system (HR sync, Exchange) owns;
- add `prevent_destroy` to critical groups.

Run `declarad fmt` and `declarad validate`.

## 4. Adopt

```sh
declarad plan --adopt
```

Every exported object should show as **taken under management** with no changes. Differences mean the configuration doesn't match reality yet: fix the configuration until the plan is clean, unless the difference is intended.

```sh
declarad apply --adopt
```

From now on these objects are managed. Removing one from the configuration **deletes it** from AD. Use `declarad state rm` to stop managing an object without deleting it.

## Alternatives

- Adopt individual objects with an [`import` block](../configuration/moved-and-import.md) (reviewed in the plan) or `declarad import <address> <dn>`.
- Reference objects you don't want to manage through [data sources](../configuration/data-sources.md).
- Add members to unmanaged groups with [`group_membership`](../resources/group_membership.md) instead of managing the whole group.
