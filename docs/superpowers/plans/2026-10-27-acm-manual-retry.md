# ACM manual retry implementation plan

> Execute inline with superpowers:executing-plans; one independent whole-change review before publication. User instruction overrides worktree/commit defaults: canonical source only, no commit or push.

**Goal:** Add Reenviar to the existing ACM contracts grid, with an independently authenticated, fail-closed command candidate and existing worker/pipeline execution.
**Architecture:** User-specified separation: DEV private API stays GET-only. A separate, unmapped command endpoint requires host authentication/authorization and a server-side business approval scoped to exact integration, immutable fingerprint, operator and window. PostgreSQL admission locks the integration, H4, current journal and attempts, records operator/reason/previous state and queues the same integration atomically. Existing motor creates the new fenced attempt; it never recreates SAP receipt. A bounded candidate worker consumes only an admitted command; no hosted/global worker is enabled.
**Tech stack:** Existing .NET 10, WinForms, Npgsql, existing auditoria_integracao/projecao_resultado_fluxo_mvp. No migration or physical QA/write in this gate.
**Spec:** User order FUGATES-DEV110-ACM-MANUAL-RETRY.

## Rulings and constraints

- Existing generic ReprocessarAsync is insufficient for business recovery; do not loosen it or global automatic admission.
- Approval must be server-owned, from a homologated evidence/authorization source. A browser/UI flag or journal absence never proves SAP provenance or remote absence.
- No such command service is currently homologated. Installed Desktop must use an unavailable command binding; display reasons and never send a command. Candidate ports/services are tested offline only, not registered in Program or the GET-only DEV mode.
- Known SOAP confirmation may resume only REST after exact immutable mapping/correlation validation. Preserve original terminal outcome in audit before resetting current stage to the existing R165U_SOAP_CONFIRMED checkpoint. Unknown/armed/timeout, completed, synthetic, stale identity, unknown origin or active/pending attempt always blocks.
- HTTP400 requires server-approved correction evidence for an external dependency/configuration, bound to observed failure and immutable H4 fingerprint. Invalid original SAP fields are never changed by this flow; source correction requires separate business flow.
- Existing schemas need no new objects. Future physical review must verify diagnostic-column SELECT and all necessary column UPDATE/INSERT ACLs; no grants executed now. Simulated store tests are not proof of PostgreSQL ACID.
- Keep 5432/5434, VM, HML services and all business operations untouched. Preserve Claude JSON and current connection profile/key. No key copies.

## Review focus

1. Replay with a new request ID cannot bypass an existing queued/started command; command identity cannot cross integration/operator.
2. Authorization and preview must be revalidated under transactional row locks; no UI state trusted for writes.
3. HTTP400/known SOAP must not erase remote-unknown evidence or silently alter payload/mapping.
4. Commit failure is unknown, never accepted or automatically retried; worker cannot run before durable admission.
5. Command transport remains absent from GET-only reader and production startup; only explicit future composition can bind it.

## Task 1 — policy, transactional command candidate and bounded worker

- [x] Add failing tests for eligibility/unknown/HTTP400/stage/identity and admission duplication/failure, then implement application retry models, policy and service under Aplicacao/Servicos/ManualRetry.
- [x] Implement PostgreSQL candidate under Persistencia/ManualRetry using existing tables, strict 5433 identity, FOR UPDATE, parameterized audit and compare-and-set. No physical execution.
- [x] Implement separate candidate endpoint under Callback.Http, requiring authenticated principal and explicit policy; do not map it in existing Program/DEV reader.
- [x] Implement bounded HostIntegracao worker adapter invoking MotorIntegracaoServico.ExecutarPorIdAsync only after one durable command-start reservation. Preserve original motor, pipeline, adapters, fencing and attempt history.
- [x] Run focused offline tests; record physical verification as not executed.

## Task 2 — existing contract grid

- [x] Write a failing STA UI test for the Reenviar action while JSON remains present.
- [x] Add a focused FrmContratosAcm.Retry partial, typed client port/controller and confirmation dialog. No business/HTTP logic in Designer or form.
- [x] Default local binding is unavailable, with read-only reasons from actual row/detail. Every unsafe row is blocked; no synthetic or unknown-origin record can send.
- [x] Show contract/item/stage/reason and require justification/confirmation if a future homologated client returns a valid preview. Guard double clicks; refresh grade and monitor readback after command/result, without saying a queued command is success.
- [x] Test UI, JSON and v1.2 connection compatibility.

## Task 3 — review, build, backup and local publication

- [x] Independent review; fix Critical/Important findings with focal regression.
- [x] Build Release and relevant regressions, no physical write profile.
- [x] Check source/installation drift, backup current DEV110, publish locally with command execution disabled, preserve all profiles/key.
- [x] Open actual executable and validate Reenviar/blocked reason/JSON/physical readback. No VM changes or command dispatch.
- [x] Deliver package/hash, exact command-homologation prerequisites and rollback.

## Verified outcome

Release 1.2.1 published locally and opened. Relevant regressions: 602 ACM + 346 core + 195 UI = 1143 PASS, zero remaining failures. An additional 54 command cases were rerun as a subset. JSON and physical readback remain operational (6 rows). Installed command client is read-only/unconfigured. No physical retry SQL, VM change, worker activation or business POST occurred. Physical ACID/ACL validation and command-service homologation remain future prerequisites, not completed by these offline tests.

Concurrent repository movement from f582c0e to b30de48 was observed and preserved. Its nine committed files match the current working bytes and predate the compiled release; no commit/reset/merge was performed by this task. See FINAL_VERIFICATION.json.
