# Toledo SAP confirmed ACK Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Return literal HTTP200 OK to Guardian only after durable SAP business confirmation, observing the existing worker for at most45seconds.
**Architecture:** Preserve the single existing worker and its durable fencing/journal. The callback observes a read-only correlated snapshot; it never dispatches or retries SAP. Keep the legacy receipt ACK outside the explicitly selected SapConfirmed mode.
**Tech Stack:** .NET10, ASP.NET Core, existing Npgsql datasource DEV5433.
**Spec:** C:/Users/TI/Documents/ChatGPT/FugaTES/evidence/TOLEDO_GUARDIAN_ACK_20261101/SAP_CONFIRMED_ACK_DESIGN.json (operator approved45seconds).

## Global Constraints
- Canonical source only; no commit/push, live POST, replay, migration, GRANT or deploy during implementation.
- Preserve Claude Program.cs/UnifiedCallbackRoutes.cs and SD/MM. Preserve name20/35/50, E/S, ID10 and all SAP mapping/auth guards.
- No positive ACK for receipt-only persistence, raw HTTP201, failed finalization, unknown result or missing observer.

## Review Focus
- A stale success from an earlier attempt must not satisfy a newer attempt.
- Concurrent duplicate callbacks must observe the same integration without starting another worker.
- Client disconnect or observation deadline must not cancel the worker or trigger another POST.
- SAP result audit can precede final journal commit; do not return OK during that interval.
- Error messages must contain fixed safe codes/text, never raw SAP errors or credentials.

## Task1: Durable observation proof and bounded waiter
Files: new src/FugaTES.Callback.Http/ToledoSapCallbackOutcomeRead.cs and src/FugaTES.Callback.Http/ToledoSapCallbackAcknowledgement.cs; tests/R158.SapInboundReadiness.Tests/ToledoSapCallbackAcknowledgementTests.cs.
Interface: IToledoSapCallbackOutcomeRead.ReadAsync(receiptId,integrationId,ct) returns a read-only snapshot including current attempt/fence, state, prepared/result audit and final attempt outcome. Use existing RetornoToledoSap.Confirmado; no new application/domain API is needed.
- [x] RED: explicit SapConfirmed mode must reject receipt-only ACK without a configured reader.
- [x] GREEN: implement snapshot validation and45second bounded observation; pending504, rejected422, unknown409, observation unavailable503.
- [x] Verify tests for durable finalization, proof/hash/attempt/fence mismatch, timeout, cancellation, malformed evidence and both confirmed SAP result variants.

## Task2: Endpoint and composition
Modify only src/FugaTES.Callback.Http/GuardianCallbackEndpoint.DualFormat.cs and ToledoSapReturnComposition.cs.
- [x] After receipt commit, SapConfirmed mode awaits the observer and returns text/plain OK only for confirmed result; safe JSON failure otherwise.
- [x] Register reader from existing dedicated datasource5433 only when SapConfirmed is explicitly selected and return worker is enabled. Unknown modes fail closed.
- [x] Keep legacy ACK and all non-Toledo callbacks unchanged.

## Task3: Verification and candidate
- [x] Run Release build and full readiness project tests; exact runtime focused tests with simulated transports only.
- [x] Review final diff; prepare hashes, protected configuration delta and rollback plan. Publication requires a separate concrete authorization.

## Rulings
- Keep the dedicated HTTP read-side observation adapter inside Callback.Http, following its existing private read API pattern, and reuse existing application result model. This permits a single Callback DLL patch without replacing Application/Persistence or touching the command/reader/desktop.
- Preserve approved canonical workspace rather than create a second workspace; preserve all parallel dirty files.

Execution: canonical Release914PASS, frozen runtime376PASS, inert startupPASS, physical read-only SQL5433PASS. No deployment or business POST. Candidate SHA256 AE9C47B27D4D0F82353673B428195D0F6568D9655D493BFF8A8DCC5013E56C96.
