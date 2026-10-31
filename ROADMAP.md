# declarad Roadmap

declarad brings infrastructure-as-code to Active Directory: version-controlled, reviewable and repeatable identity management.

## ✅ Phase 1 — MVP
- [x] HCL parsing and mapping to the AD model
- [x] `plan` and `apply`, color-coded diff output, verbose logging
- [x] State keyed by objectGUID (renames, moves, deletes, drift)
- [x] `export` of existing AD objects to HCL
- [x] LDAPS/StartTLS/LDAP with simple and negotiate auth

## ✅ Phase 2 — Language and developer experience
- [x] Full HCL expression language: operators, splat, `for` with grouping, template directives, heredocs
- [x] Terraform-compatible function library, plus DN helpers
- [x] Typed variables (`object({...})`, `optional()`), validation blocks, preconditions
- [x] `count`, `for_each`, `depends_on`, `lifecycle`
- [x] Local modules with `for_each`/`count`, outputs
- [x] Data sources (objects, GPOs, domain, LDAP search)
- [x] `moved` and `import` blocks, `--target` with dependency closure
- [x] `fmt`, `validate`, `schema`, `graph`, `output`, `state list/show/rm/mv`

## ✅ Phase 3 — Resource coverage
- [x] OUs (with block inheritance and deletion protection), containers, groups, users
- [x] Computers, contacts
- [x] Group managed service accounts (gMSA)
- [x] Fine-grained password policies
- [x] Additive group memberships for unmanaged groups
- [x] GPO links (order, enforced, enabled)

## 🚀 Phase 4 — Team scale
- [x] Remote state backends (S3, Azure Blob, HTTP) with locking, migration, pull/push
- [ ] Saved plans (`plan -out`, `apply <planfile>`)
- [x] Module sources from git and HTTP archives
- [ ] Delegation (ACL) resources on OUs
- [x] DNS zones and records in AD-integrated zones
- [ ] GPO creation and settings (requires SYSVOL access)

## ✨ Phase 5 — Differentiation
- [ ] Local web dashboard
- [ ] Scheduled drift detection with Slack/e-mail alerts
- [ ] Entra ID (Azure AD) provider over Microsoft Graph
- [x] Multiple connections (several domains/forests in one configuration)
