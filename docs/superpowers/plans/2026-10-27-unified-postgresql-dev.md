# Unified PostgreSQL DEV implementation plan

Spec/authorization: HERA V4 FUGATES-UNIFIED-POSTGRESQL-DEV and complementary SAP contract/JSON order. Canonical source only. Preserve Claude commit f582c0ecf02ce3a2eaa04e7cb8fca09997c4e2e0 and existing worktree. No main merge/push, external business POST, destructive DDL or HML datasource/service change.

## Physical findings

- Inventory completed with PostgreSQL 18 READ ONLY/ROLLBACK on ports 5433, 5434 and historical 5432. No writes.
- Target 5433: nine ACM tables, six H4 versions / six contracts. One is the proven synthetic 0000986801. The other five persisted rows must not be relabeled as authenticated SAP/CPI deliveries without provenance. Historic 5432 contains 17 versions / 14 contracts; it is not copied under this gate.
- payload_contrato is already jsonb and non-null. Preserve the Claude repository's Item extraction and payload_contrato::text; no payload_bruto migration.
- Source MasterData 5434: only fugates.carrier_single_create_journal, seven columns, four CHECKs, one PK, six PostgreSQL 18 NOT NULL constraints and three unique indexes. No functions/sequences required. Target has no duplicate MasterData journal.
- TI can open the existing private key without reading bytes; ACL is restricted to TI/SYSTEM. No key copy or permission expansion is necessary. Claude's isolated execution identity has not been attested.
- Empty contract grid has a concrete composition cause: Claude's JSON button calls the legacy composition requiring DesktopRuntime.Current, while unified startup deliberately avoids that corporate runtime. R165AL additionally exports only one validated integration, not the contract list/persisted projection.

## Chosen architecture

Retain one WinForms and the private authenticated loopback API architecture. Extend the canonical Callback.Http host with an explicit, isolated DEV read-only mode, reusing the existing PostgreSQL factory and read repositories. No business compositions, callbacks or workers are registered in that mode. Keep the old pilot readback contract compatible, and expose bounded monitor/contract/JSON/journal reads from the same 5433 datasource and legitimate existing provider. Never create a privileged Windows database connection.

Add a separate masterdata schema in the target only, owned by the existing fugates_dev_owner. Derive the journal table from approved R150A and reconcile it against the physical source. No rows copied, no new login/credential, no modification of existing ACM tables. Give the existing application identity only USAGE/SELECT on the new read objects; do not grant MasterData writes, CREATE, DELETE or TRUNCATE. The source HML guards and services remain unchanged. CarrierSync R3 is not a prerequisite for this SingleCreate journal and is not installed speculatively.

## Execution tasks

- [x] Read-only physical inventory and snapshots; preserve Claude/source/installation checkpoints.
- [x] Prepare an additive, idempotent target-only migration and postcheck; independently review object/ACL equivalence, dependencies and failure behavior.
- [x] Validate the current target backup: consistent PostgreSQL 18 custom archive, TOC/full archive read, exit codes/hash, private permissions. Reuse historic restore evidence and distinguish it from any new restore test. Never restore onto operational databases.
- [x] Confirm identity and absence of unexpected writers; apply only after backup and plan review. Transactional migration; stop on any error/unknown COMMIT. No automatic retry or destructive rollback.
- [x] Add/test canonical read-only API mode with strict physical identity, existing passfile/key references, constant-time key validation, GET-only routes, bounded responses, no logs/payload export and no business registration.
- [x] Inject the private reader into the existing contract/JSON and carrier screens. Preserve Claude's JSON representation and item semantics. Label persisted projection and known simulation accurately; never fill absent fields.
- [x] Extend the same monitor reader to the available 5433 records. Preserve the proven pilot integration and fail closed on credential, identity or backend errors.
- [x] Diagnose missing vs denied key metadata without new copies or ACL changes. Tests must not consume real secrets.
- [x] Build Release and relevant focal regressions; validate actual application-role SELECT/ACLs on the new schema. No business writes.
- [x] Coordinate shared publication using the confirmed Claude commit and before/after hashes, preserve rollback and HML services. Any Linux DEV API publication must remain isolated and within demonstrated authorization; never modify HML services.
- [x] Backup/update/open the same DEV110 executable; validate six actual persisted records, JSON button, MasterData empty-state/physical query, connection status and close/reopen behavior.

## Executed outcome

Evidence: C:\Users\TI\Documents\ChatGPT\FugaTES\evidence\UNIFIED_POSTGRESQL_DEV_20261027.
Target migration and the separately reviewed twelve column SELECT grants committed. No business rows changed or copied. Existing ACM rows retained; 5432/5434 structure unchanged.
The canonical read host runs only its explicit DEV GET mode on 127.0.0.1:18762. It consumes existing provider references; old pilot read API 18761 remains intact. No shared HML service/datasource was modified.
Actual application-role GETs passed identity, contracts, monitor, MasterData, protocol and detail. Six H4 projections are available; only the pilot is positively classified as synthetic. Missing Item in five projections remains missing; 1147 has Item 10. All six persisted JSONs parsed successfully. This does not attest original SAP/CPI delivery.
DEV110 1.2.0 opened, displayed the six rows and JSON 1147/item 10, closed gracefully and recovered its owned tunnel on normal launch without arguments. MasterData reports zero DEV journal rows and CREATE remains disabled.
Release solution build passed. Initial regression: 2918 passed, 2 failed, 1 physical skipped; both initial failures passed in isolated recheck without production changes. Final UI delta: 190 passed. Keep the initial results for transparency.

## Stop/recovery criteria

Unknown schema drift, concurrent source/runtime mutation, unknown writer, insufficient provider/privilege, unvalidated backup or indeterminate COMMIT stops only the affected phase. Do not fabricate historical rows or copy HML business records to make the grid non-empty. Restore application bindings/binaries from verified backup when necessary; retain additive unused schema objects for controlled review rather than dropping them. Do not change 5432/5434, old role login, firewall, Nginx or shared HML runtime.
