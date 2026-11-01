# Pending latest ACM recovery implementation plan

Goal: prepare safe unitary processing of the existing latest1175 version with saldo3000 through Reenviar.
Architecture: extend existing server-authorized retry mode and transactional repository, with exact manual dynamicemitter context; no newservice/schema/engine. .NET10/C#/PostgreSQL18/xUnit. Spec docs/superpowers/specs/2026-10-31-acm-pending-latest-recovery-design.md.

## Constraints / review focus
Canonical source only; preserve dirtywork and historicalpackages; no commit/push/deploy/DML/realGuardian call. Defaultdeny existing approvals unchanged. Do not inferItem, identity or businessdata. Pendinglatest is not a fictitious failed attempt. Proven latest source and bounded predecessor supersession in one transaction. Unknown/concurrent/historicalremote conflict blocks. Exact nullItem matches null only. No globalcutoff/backlog or secondworker. No SOAP repeated after confirmedlink.

- [ ] Write synthetic policy/profile/runtime-boundary tests; run RED on current code.
- [ ] Extend AcmManualRetry.cs with explicit PendingLatest approval, authoritative snapshot/predecessor metadata and fail-closed eligibility.
- [ ] Extend AcmManualRetryPostgreSql.cs: read pending target without journal, authoritative predecessor facts, deterministic contract locks, supersession audit/CANCELADA and target queue atomic; retain original histories. Distinct start path for pending target with no fabricated journal checkpoint.
- [ ] Extend profile/host and normal scope/resolver to exact manualtarget/version/hash/expiry, nullableItem and shared approved businessscope; preserve ordinary dynamiccutoff, materialguard and mandatoryabsencecheck.
- [ ] Run focused and applicable full offline regression, build LinuxRelease, independent review; produce one versioned candidate with hashes and physical read-only precheck/promotion/rollback. No installation/realdispatch yet.


## Final operator decision — SOAP only

The operator chose only SOAP registration of1175; protocol remains explicitly pending. New PendingLatest approvals require SoapOnly, not SoapThenRest/RestOnly. Server scope carries SoapOnly=true; pipeline commits confirmedSOAPidentity then persists R165U_PROTOCOL_BLOCKED/ACM_MANUAL_SOAP_ONLY_PROTOCOL_NOT_AUTHORIZED before protocol preparation/REST. Default existing modes remain unchanged. WinForms confirmation clearly labels SOAP-only/protocolpending. Client permits Itemnull only for exact PENDENTE/PENDING_LATEST_SAFE_ADMISSION/SoapOnly response; generic nullableItem acceptance is not introduced.

Release builds PASS;645motor+104focusedcore+70UI=819finaluniquePASS; exactpackagedbinaries645motor+104core+70UI=819PASS. Four fullcore failures reproduced identically with pre1175DLLs are documented in baselineproof, not hidden. Independentreview zero materialfindings. NewSQLSELECTs passed READONLY/ROLLBACK/EXPLAIN; no recoveryDML, realPOST, deployment or unitapproval creation. Candidate has5DLLcommanddelta+3DLLDesktop, inactivebindings and controlledpublication/rollback. Latestchecked atomically atadmissionandStart; version2 immutable afterworkerStart, no silentchasing laterSAPversions. Await separate packagepublication/operationalauthorization before mutating installation or executing.
