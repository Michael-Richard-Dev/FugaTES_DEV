# Parceiro do contrato SAP na pesagem Toledo — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. No new workspace, commit or push: user requires canonical source only.

**Goal:** IdParceiro derives from the persisted SAP contract; IdParceiroEntrega stays exactly from Guardian.
**Architecture:** Resolve authoritative contract snapshots in the existing read phase; project only IdParceiro in the existing SAP gateway. Journal the immutable per-item binding before POST. Existing JSON SAP readback reuses that binding and verifies exact hashes. No migration or additional motor.
**Tech Stack:** .NET10 / Npgsql / WinForms.
**Spec:** User-approved two-field mapping in this conversation. Physical READ ONLY evidence: contract0000001168, center3011, material000000000007000000, partner0020000610.

## Global Constraints
- Original Guardian document, original fingerprint, IdParceiroEntrega and all unrelated fields preserved.
- Numeric contract/material identifiers may differ only in leading zero width; never infer SAP Item from Guardian Indice.
- Source must be SAP/TOLEDO/SAP_CONTRATO_TOLEDO with valid canonical fingerprint and matching integration.
- Use latest source version per SAP item available at receipt time; ambiguous partners, absent contract or malformed evidence block before any SAP HTTP.
- No real POST/replay, no DDL/GRANT, no secret changes, no main merge/push.
- Publication of changed writer binaries is separate from offline implementation approval.

## Review Focus
- Multiple weighing items/contracts; correct partner per row.
- Multiple SAP items/versions, ambiguity and future versions.
- Guardian delivery BP preserved including leading zeros, never filled by fallback.
- JSON SAP historical reproduction uses audited mapping, not a changed live contract.
- Existing E/S, ID10, NF-e, timezone, CSRF, journal, fencing and retry rules remain intact.

### Task1: Resolve and project contract partners
- [ ] RED test: missing contract source blocks before any SAP request.
- [ ] Implement source/binding DTOs and pure resolver with provenance/version checks.
- [ ] Load candidates in existing read transaction; map IdParceiro only in strict gateway projection.
- [ ] Tests: PF/PJ delivery differences, multiple contracts, leading zeros, absent/ambiguous/tampered sources, original preservation.

### Task2: Preserve audit and JSON SAP readback
- [ ] Add binding metadata to the existing pre-POST plan; no new table.
- [ ] Whitelist binding metadata in GET-only audit reader; current previews resolve persisted source, historic documents replay audited binding only if SHA matches.
- [ ] Tests: differing IdParceiro appears in exact outbound JSON and audit; historical exports still match; invalid binding cannot become a sent document.

### Task3: Release preparation
- [ ] Release build, focused regression, independent review.
- [ ] Versioned package, file hashes, rollback and physical read-only check for contract1168.
- [ ] Present concrete writer publication scope for approval; no business test/replay.
