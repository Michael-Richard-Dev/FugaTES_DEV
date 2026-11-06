# MM Guardian automatic dispatch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement task-by-task. No commits without explicit user authorization.

**Goal:** Adapt the completed SD protocol flow for automatic purchase-order/item dispatch from the existing MM collector.
**Architecture:** MM mapper and Guardian gateway follow SD wire conventions; a separate MM PostgreSQL registry reserves before transmission. The existing collector runs one SAP polling loop and one sequential dispatch consumer while the Form reads snapshots.
**Tech Stack:** .NET 10, WinForms, Npgsql/PostgreSQL18, xUnit, existing Windows credential providers.
**Spec:** docs/superpowers/specs/2026-11-01-mm-guardian-automatic-design.md (approved 01/11/2026).

## Global Constraints
- Canonical source only. No SD, ACM or Toledo functional changes; no commit/push, deployment, physical DB connection or business POST during implementation.
- SAP110/company3000/plants3011,3008/all approved types; initial existing and subsequent new items eligible after operational reconciliation.
- POST only approved HTTPS Guardian route and existing token provider; exact ten additional fields and one protocol/item.
- Strict MM identity, DEV5433 and independent MM table; migration/grants remain candidates.
- Never retry a reserved, rejected, unknown or completed item automatically. UI does not initiate HTTP.

## Review Focus
- Canonical identifiers with leading zeroes must collide rather than create duplicate protocols.
- Failure persisting a result after external success must leave the reservation occupied.
- Slow Guardian responses must not stop 30-second SAP polling or create parallel sends.
- An old cached successful SAP result must not authorize dispatch after a failed/partial current poll.
- A malformed/ambiguous success response must not be classified as created.

## Task 1 — Mapping and wire gateway
Files: Aplicacao/Modelos/Mm/ProtocoloPreCadastroMm.cs; Aplicacao/Servicos/Mm/MapeadorProtocoloMm.cs; Integracao.Guardian/GuardianProtocoloMmClient.cs; tests/R158.SapInboundReadiness.Tests/MmGuardianProtocolTests.cs.
Interfaces: MapeadorProtocoloMm.Montar(PedidoCompraMmLeitura,string), ChaveNatural(PedidoCompraMmLeitura), Serializar(ProtocoloPreCadastroMmRequest); IEnviarProtocoloMmGateway.EnviarAsync(byte[],CancellationToken) -> ResultadoProtocoloMm.
- [ ] RED: missing mapper, mapping E/plant/purchase order/item, real Supplier/Material and zero fields.
- [ ] GREEN: types/mapper; strict HTTPS callback without credentials or fragments, approved source row and identifiers.
- [ ] RED/GREEN: gateway fake token/transport; exact destination, payload bytes, timeout, no redirects/retries, positive protocol ID, 400 safe diagnostic, ambiguous200 indeterminate.

## Task 2 — Registry and dispatch service
Files: Aplicacao/Contratos/Mm/IRegistroProtocolosMm.cs; Aplicacao/Servicos/Mm/EnvioAutomaticoMm.cs; Persistencia/Mm/RegistroProtocolosMmPostgreSql.cs; docs/Incrementos/007/MIGRATION_007M1_PROTOCOLO_MM.sql; ROLLBACK_007M1_PROTOCOLO_MM.sql.
Interfaces: registry LerAsync, TentarReservarAsync, FecharAsync; EnvioAutomaticoMm.ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura>,CancellationToken) -> counts/status. Reservation includes key, original identifiers, payload bytes/hash, timestamp and reservation UUID; close requires matching UUID and open state.
- [ ] RED/GREEN with in-memory registry and simulated gateway: reserve before send, storage failure/no send, repeated cycles, concurrent processors, exact JSON, timeout blocked, persistence failure, terminal responses and changed payload blocked.
- [ ] Implement strict table and registry; candidate SQL owns fugates_dev_owner and app fugates_mm_app only, no old-role grants, unique keys/protocols and no destructive rollback with data.

## Task 3 — Collector and operational composition
Files: WinForms/Mm/MmGuardianComposition.cs; ConexaoMmPostgreSql.cs; MmPurchaseOrderComposition.cs; MmPurchaseOrderCollector.cs; MmSnapshotStore.cs; tests/.../MmGuardianCollectorTests.cs.
- [ ] RED/GREEN: optional dispatch, current complete results only, continuous independent polling and sequential dispatch; stale/failed polls revoke remaining admission, no background lifetime leak.
- [ ] Compose existing Windows SAP/Guardian providers, protected MM profile and tunnel; validate actual cluster/role/5433 before registry writes. Default disabled, explicit reconciliation reference/callback/activation reference required for live mode. Missing DB/profile keeps SAP reading alive and sending blocked.
- [ ] Save immutable readback observations through the single snapshot writer. Raw cache is a display copy, not the durable registry.

## Task 4 — Official MM screen
Files: WinForms/FrmPedidosCompraMm.cs; tests/.../MmGuardianMonitorTests.cs.
- [ ] RED/GREEN: preserve JSON SAP and filters, add JSON Guardian/HTTP/protocol/state/timestamp columns, repaint when dispatch result changes without new SAP success.
- [ ] Form reads snapshots only; no manual HTTP or second sender. Mark preview vs persisted transmitted JSON accurately.

## Task 5 — Verification and candidate
- [ ] Release build and focused MM/SD shared-contract tests with simulated transports; inspect every result. Broaden only when shared project changes justify it.
- [ ] Independent final review; fix material findings and rerun affected tests.
- [ ] Versioned candidate, manifest/SHA-256, source delta, SQL candidates and safe configuration examples. Preserve current official install.
- [ ] Record exact activation prerequisites: approved MM credentials/schema, callback URL, historical remote reconciliation and coordinated publication. Never manufacture these proofs.

## Execution ledger
Native execution in this session, as the continuation of the approved implementation. User AGENTS canonical-only/no-commit requirements override generic worktree and commit steps. Track completed tasks and rulings here; preserve all parallel files.
