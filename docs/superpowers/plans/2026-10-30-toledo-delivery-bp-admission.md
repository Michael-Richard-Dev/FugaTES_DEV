# Toledo delivery BP validation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. User requires canonical source only, no new worktree, commit or push.

**Goal:** Validate nonzero Guardian IdParceiroEntrega as an SAP supplier before durable acceptance; enrich ParceiroEntregaNome from SupplierName. Blank/zero defaults copy original IdParceiro/ParceiroNome.
**Architecture:** Existing admission use case calls a read-only SAP adapter; original JSON remains unchanged. Persist a typed per-item validation proof in existing auditoria_integracao inside the original receipt transaction. Existing SAP projection and JSON SAP readback consume that frozen proof. Existing contract-owner rule, claim/fencing and ACK-after-COMMIT remain.
**Tech Stack:** .NET10 / WinForms / Npgsql / SAP ODataV2.
**Spec:** User request in this conversation; Especificação consulta de BP.pdf pages7–10,20–21,33,35. Source identity in evidence/SPEC_REFERENCE.json.

## Global Constraints
- IdParceiro remains the authoritative SAP contract owner, as approved1.2.9.
- Nonzero IdParceiroEntrega keeps the Guardian code exactly; supplier identity may differ only in leading zero width. SupplierName is from SAP.
- Missing/null/whitespace/all-zero delivery code copies original IdParceiro and ParceiroNome, with no remote query.
- Invalid/non-supplier/mismatched/blocked SAP responses fail before registration and queue admission. Technical/auth/timeouts never mean nonexistence.
- GET only; no CSRF or business POST for lookup. Reuse FUGATES protected authentication, fixed approved HTTPS origin/client110, no automatic retries.
- Validate every item before any receipt commit; one bad item rejects the whole receipt. Deduplicate GETs per BP/request.
- Original document, original fingerprint and idempotent receipt identity stay unchanged. Re-delivery of a proven persisted receipt uses frozen validation; no new GET or redispatch.
- No migration, new service, GRANT, secret copy, business test/replay, Nginx or HML change during implementation.

## Review Focus
- BP exists but is not Supplier; missing or wrong Supplier identity/name/account group.
- 401/403/HTML404/timeout/invalid JSON cannot be classified as BP not found.
- Blank/zero fallback must copy the ORIGINAL Guardian fields, not the subsequently projected contract owner.
- Concurrent identical callbacks persist a single validation snapshot with the original transaction; commit failures never ACK.
- JSON SAP historical reconstruction uses the audit proof, never a newer live SAP supplier name.

### Task1 — Admission GET and source validation
- [ ] RED: explicit BP with no validation capability must not ACK/persist.
- [ ] New Application supplier result/port + typed proof/default rule/validator. Existing use case invokes it before store, with read-only duplicate lookup for explicit BP.
- [ ] New SAP adapter using existing bounded GET executor/authentication; SupplierName and Supplier identity verified.
- [ ] Existing callback catches business/technical validation exceptions with safe field/indice/code/message. Existing composition registers the existing provider and read-only adapter.

### Task2 — Durable proof and SAP projection
- [ ] Original receipt and proof use one transaction; existing duplicate path never overwrites validation.
- [ ] Existing application/persistence/read models carry the frozen proof; no new table.
- [ ] Apply only IdParceiroEntrega and ParceiroEntregaNome in outbound projection, then existing authoritative IdParceiro mapping.
- [ ] Include proof in existing pre-POST audit; readback reconstructs exact body only with matching hash, preserving old audited receipts.

### Task3 — Verification and release
- [ ] Tests for valid/missing/blocked/invalid supplier; fallback; multiple items; HTTP response; no persistence on rejection; idempotency; commit failure; existing E/S,ID10,NFe/JSON UI.
- [ ] Release build, independent review, exact-runtime package verification.
- [ ] Prepare versioned package and rollback; request nominal publication only for the concrete tested candidate. Operator tests NEW weighing on contract1168 after installation. No real weighing POST by Codex.
