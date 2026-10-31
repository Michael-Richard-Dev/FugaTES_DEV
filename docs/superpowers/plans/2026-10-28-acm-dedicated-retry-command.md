# ACM dedicated retry command — authorized implementation plan

User explicitly authorized the dedicated command service on VM, exclusively ACM 5433, separate from GET-only API/existing HML services. No migration, test POST Guardian/SAP or historical replay. Column SELECT worker/diagnostico is physically absent and has a separate pending exact grant authorization. No commit/push or alternate source workspace.

Design: reuse existing AcmManualRetryService, AcmManualRetryPostgreSql, bounded AcmManualRetryWorker and original motor/adapters. Dedicated startup flag and loopback port 18763; ordinary callback/SAP/read API startup unchanged. Dedicated protected Bearer credential maps to one server-configured Windows SID, never trusting a submitted SID. Exact server-owned approval/evidence records are external, private, scoped to integration/fingerprint/snapshot/stage/window; no approval fabricated for existing unsafe historical rows. Empty approval list blocks commands. Each accepted command wakes one bounded existing worker; no global poller/backlog or automatic retry after unknown outcome.

Desktop: optional dedicated command profile and credential file outside source/package, TI/SYSTEM-only; exact current Windows SID, dedicated verified SSH command tunnel (18763, never read API ports); no fallback to read key, Guardian credential, direct DB or HTTP from form. Existing controller confirmation/justification and latched unknown behavior preserved. Recovered REST skips confirmed SOAP. Validation uses offline transports; physical smoke is GET preview/health only, no enqueue/DML or business POST.

- [x] Host startup/authentication/approval composition, bounded worker scheduling, focal offline tests.
- [x] Windows command binding/tunnel/credential validation, blocked-preview compatibility, focal tests.
- [x] Root: physical ACL check; apply only separately authorized minimal grant; prepare versioned protected installation/rollback.
- [x] Independent review, build/regressions, package/hashes; install isolated service and verify identity + negative/GET-only smoke.
- [x] Bind/release local Desktop; real business dispatch only on operator confirmation for proven eligible target. No CREATE or retry test from agent.

Ruling: published DEV110 1.2.2 and Claude b30de48 remain intact until a new verified local package is ready. Do not label synthetic/unknown historical cases as eligible. No security constraint is replaced by an authorization flag.

Verified: host installed/authenticated on18763; exact user-authorized GRANT2 columns committed/readback; protected credential copy explicitly approved after auto-review rejection; Desktop1.2.3R2 configured/readback/reopen verified. 325.NET+3installer offline tests pass. Current approvals remain empty because no proven safe target exists; zero real business requests/DML. Per-target business readiness is not declared.
