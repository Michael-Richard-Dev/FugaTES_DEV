# MM / ACM environment pairs implementation plan

> Execution: native, as requested by the operator; no deployment or VM access.

**Goal:** preserve DEV110 and admit QAS120 only through explicit, indivisible SAP/database pairs.
**Architecture:** consume the existing registries without editing them. Configured SAP selects exactly one database environment. Shared guards retain DEV110 defaults and receive explicit accepted environments. Physical checks retain all existing host, port, schema, passfile and read-only controls.
**Spec:** operator instructions of 06/11/2026 in this conversation.

## Constraints and review focus
- Do not edit reserved registry/factory/identity/SD/deploy files. Preserve concurrent changes.
- No network, database writes, migrations, deployment, activation, push, or business replay.
- Reject DEV SAP/QAS client, QAS SAP/DEV client, cross database/role, and valid pairs belonging to the wrong process.
- Missing SAP profile retains legacy DEV only where it previously existed; partial or invalid pairs fail closed.
- Do not add Search Path requirements to existing motor connections.
- MM snapshots, retry stores and worker leases must carry the same selected environment as SAP.

## Tasks
- [x] Commit pending MM/catalog dependencies first (f096a85), preserving reserved work.
- [x] MM: failing options tests; parameterized physical identity, registry/retry/lease; VM adapter receives configured SAP environment; Windows profile derives database from its SAP profile.
- [x] ACM: configured pair in provider, motor composition and dedicated commands; explicit database/role propagation into identity guard.
- [x] Readback: parameterized monitor SQL, private API, envelopes, physical/Desktop profiles; reject a valid envelope from a different selected environment.
- [x] Run correct/cross pair tests and existing port/host/schema/passfile negatives; mutate each converted guard, verify diff, record killed mutants, restore exact bytes.
- [x] Release build, R158 and FugaTES.Testes; report named pre-existing failures separately. No VM publication.

Files: MM options/composition/persistence/lease/command/Desktop; ACM provider/host/command/persistence; private read API/store/monitor repositories/Desktop. New tests under R158; no global refactor.

## Final verification
{"build": "PASS", "mutations": 23, "sourceStable": true, "suites": [{"name": "R158.SapInboundReadiness.Tests", "counts": {"total": "1776", "executed": "1776", "passed": "1776", "failed": "0", "error": "0", "timeout": "0", "aborted": "0", "inconclusive": "0", "passedButRunAborted": "0", "notRunnable": "0", "notExecuted": "0", "disconnected": "0", "warning": "0", "completed": "0", "inProgress": "0", "pending": "0"}, "failures": []}, {"name": "FugaTES.Testes", "counts": {"total": "1863", "executed": "1862", "passed": "1860", "failed": "2", "error": "0", "timeout": "0", "aborted": "0", "inconclusive": "0", "passedButRunAborted": "0", "notRunnable": "0", "notExecuted": "0", "disconnected": "0", "warning": "0", "completed": "0", "inProgress": "0", "pending": "0"}, "failures": ["FugaTES.Testes.Inc005MotorIntegracaoOfflineTests.SegurancaInc005_NaoContemTransporteRealCredenciaisOuDdlExecutado", "FugaTES.Testes.SapBusinessPartnerReadOnlyTests.Seguranca_CodigoProdutivo_NaoContemRegressoesDoIncremento001"]}, {"name": "FugaTES.WinForms.Monitor.R165M.Tests", "counts": {"total": "295", "executed": "295", "passed": "295", "failed": "0", "error": "0", "timeout": "0", "aborted": "0", "inconclusive": "0", "passedButRunAborted": "0", "notRunnable": "0", "notExecuted": "0", "disconnected": "0", "warning": "0", "completed": "0", "inProgress": "0", "pending": "0"}, "failures": []}, {"name": "R165R.Provider.Tests", "counts": {"total": "59", "executed": "59", "passed": "59", "failed": "0", "error": "0", "timeout": "0", "aborted": "0", "inconclusive": "0", "passedButRunAborted": "0", "notRunnable": "0", "notExecuted": "0", "disconnected": "0", "warning": "0", "completed": "0", "inProgress": "0", "pending": "0"}, "failures": []}]}

Pending-work commit: f096a85. Environment conversion remains a local, uncommitted delta. No VM publication or DEV110 executable change. Reserved source files unchanged; concurrent deploy changes preserved. Full evidence: evidence/ENVIRONMENT_PAIRS_20261106/FINAL_REPORT.json in the operations workspace.
