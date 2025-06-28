# Entraform Roadmap

This roadmap outlines the feature development path for **Entraform** — a declarative Active Directory management tool using HCL.

---

## ✅ Phase 1 — MVP (Core Usefulness & Uniqueness)
Focus: Core functionality and proving uniqueness

- [x] HCL parsing and mapping to AD model
- [x] `plan` command for change preview
- [x] `apply` command to enforce config
- [x] Verbose logging with `-v`
- [ ] Human-readable, color-coded diff output
- [ ] Export current AD state to HCL
- [ ] Robust AD connection and error reporting

---

## 🔧 Phase 2 — Polish & Developer Experience
Focus: Usability, feedback, and safe onboarding

- [ ] `entraform init` to bootstrap config
- [ ] HCL config validation / linter
- [ ] Inline comments in generated HCL
- [ ] Drift detection (`validate` mode)
- [ ] Dry-run mode with zero side effects

---

## 🚀 Phase 3 — DevOps & Team-Scale Usage
Focus: GitOps compatibility and team automation

- [ ] CI/CD pipeline-friendly exit codes
- [ ] Command-line AD credential override
- [ ] Change log system (e.g., SQLite or JSON files)
- [ ] Plugin system skeleton (e.g., for GPO support)
- [ ] Read-only HTTP API for preview/export

---

## ✨ Phase 4 — Differentiation & “Wow” Factor
Focus: Features that impress and expand audience

- [ ] Local-first web dashboard (optional)
- [ ] Scheduled `apply` (e.g., cron-style execution)
- [ ] Slack/email notifications for drift or failures
- [ ] Git pre-commit hook for HCL validation
- [ ] Role simulation mode (test as another AD user)

---

## 📅 Suggested Timeline
| Timeframe    | Goals                                                      |
|--------------|------------------------------------------------------------|
| Month 1      | Deliver MVP (Phase 1) — users, groups, OUs, plan/apply     |
| Month 2      | Add export, validation, and polish CLI interface           |
| Month 3      | Implement drift detection and begin plugin interface       |
| Month 4–5    | Add web dashboard and GitOps-friendly integrations         |
| Month 6+     | Expand to support GPOs, Intune, AzureAD, and hybrid setups |

---

> Entraform aims to bring infrastructure-as-code principles to Active Directory — making identity management safe, version-controlled, and team-friendly.

