# Toledo/SAP explicit continuous activation

> Use superpowers:executing-plans and test-driven-development. Canonical source only; no commits or push.

Goal: operator explicitly requested always-open automatic forwarding after successful live1.2.10 test. Replace daily expiry with an explicit continuous mode, preserving new-receipt admission and every business guard.
Architecture: ContinuousEnabled=true must be explicit; UntilUtc must be absent. Internal expiry default means no end ONLY with explicit continuous flag. Bounded legacy mode still requires UTC end and <=24h. SQL uses typed continuous flag to bypass only upper time bound, never lower bound, operation, recorded authorization, source, idempotency, lease/fencing or state. No artificial distant future date. Dedicated operator profile carries explicit continuoAutorizado flag and omitted fimUtc; target/revision justification and durable proof remain required. Static approvals cannot become unscoped permanent authorizations.
Spec: direct user request in this conversation, 31/10/2026: remove window limit and leave open permanently. Existing validated1.2.10 flow is reference.

## Constraints
- Keep original admission lower cutoff and recorded authorization reference; no backlog replay.
- Continuous activation explicit at all applicable server layers; missing/ambiguous config fails closed.
- Manual retries still nominal per receipt/current revision and proven pre-POST failure; completed/unknown/HTTP400/synthetic blocked.
- Same dedicated5433/provider/services18086/18763, existing private reader18762 and officialDEV110 only.
- No migration/GRANT, credential change, real POST test, replay, Nginx/8080/5432/5434 change.

## Files
AdmissionWindow, WindowRetryApprovals, ReenvioToledoSapService; ReturnComposition/worker and CommandComposition; LeaseRecovery, DevolutivaPostgreSql, ReenvioPostgreSql; version files only in WinForms. No SOAP/REST payload or supplier changes.

## Tasks
1. RED configuration test for explicit continuous mode accepting new receipt after365days; absent/malformed/ambiguous mode must reject. Implement focal common activation rules and wire automatic+operator scope.
2. Preserve SQL scope with explicit continuous parameter in selection/pre-POST/lease/retry; test future admission, old receipt rejection and all retry guards. Build Release and applicable regression; independent focal review.
3. Package minimal changed DLLs over exact1.2.10 runtime; verify exact binaries. User request authorizes installing continuous behavior; prepare backups/rollback including prior bounded configuration, no business POST test. Inert staged startup, verify no active/pending work, apply config atomically with same lower cutoff/reference and restart only18086/18763. Update reader/DEV110; verify installed hashes and GET readback; open official UI.
