> Current status 2026-10-28: dedicated command host and Desktop binding are installed/authenticated in DEV110 1.2.3 R2. No current business target is approved/evidenced, so no retry was dispatched. See the dedicated command host README and evidence/DEV110_MANUAL_RETRY_SERVICE_20261028/REPORT.txt. The v1.2.1 candidate description below is historical and preserved.

# ACM manual retry — candidate command contract

Gate: FUGATES-DEV110-ACM-MANUAL-RETRY. Baseline 1.2.0; Desktop candidate 1.2.1.

The installed Desktop uses ReadOnlyAcmManualRetryClient. There is no operational command URL/credential/profile and no configuration switch that enables it. No new endpoint is mapped in Program.cs or the GET-only DEV mode. Existing SAP ingress, automatic worker admission, SOAP/REST adapters, read key and database profile are unchanged.

## Implemented candidate

- Application: AcmManualRetryPolicy/Service; immutable H4 fingerprint, actual integration, completed/active/unknown/synthetic blocks. Approval is server-owned and bound to the exact failed snapshot digest, operator, stage and UTC window. Neither the browser nor the Desktop supplies provenance/approval flags.
- Persistence: AcmManualRetryPostgreSql uses only existing integration, H4, attempts, projection and audit tables. Admission is SERIALIZABLE, locks existing records, verifies physical 18/fugatesacmdev:5433/fugates_dev/application role, rechecks server time, persists request/operator/justification/approval/original state, prepares the existing checkpoint and queues the same IntegrationId in one COMMIT. No new H4 or attempt is inserted by admission. Motor claim creates the next fenced attempt.
- REST recovery: only proven SOAP checkpoint plus matching requested/confirmed contract code/reference/mapping hash. Original failure/metadata remains in the audit. The current projection is reset to the existing R165U_SOAP_CONFIRMED checkpoint; the original pipeline skips SOAP. HTTP400 requires approved external correction evidence bound to that exact failure. Original SAP payload is never changed; source-data correction requires another authorized flow.
- Worker: AcmManualRetryWorker requires a restricted existing motor scope, explicit globalWorkerEnabled=false and one approved integration. It consumes at most one command, persists STARTED before executing, rechecks UTC deadline after database waits, propagates deadline cancellation and records outcome/new attempt independently. Unknown start/commit/outcome never retries automatically.
- Authentication: AcmManualRetryEndpoint is a candidate plugin only. Its future host must configure a verified operator authentication scheme and policy AcmManualRetry. The handler derives ClaimTypes.PrimarySid from the authenticated principal; X-Forwarded-For, loopback or a submitted SID are never authorization. JSON cannot override actor/provenance. Routes: GET /api/v1/acm/manual-retry/{integrationId}/preview and POST /api/v1/acm/manual-retry/{integrationId}. POST returns 202 only after durable admission, not Guardian success.
- Client: AuthenticatedAcmRetryCommandClient is separate from the form and never instantiated in this release. It targets a separately approved command tunnel, excludes read API ports 18761/18762 and all external hosts, uses a dedicated Bearer credential from a future approved provider, disables proxy/redirects/cookies and sends each command once. The existing read key must not be reused.
- UI: existing Contratos/JSON grid adds Reenviar, local reasons and on-demand detail. An eligible homologated preview requires a confirmation dialog with contract/item/stage/reason and justification. Controller prevents simultaneous/duplicate sends and latches unknown results. After a committed command, only readback polling/monitor refresh occurs.

## Required before any operational enablement (NOT authorized/executed here)

1. Homologate the separate command host, authentication/provider and operator-to-PrimarySid mapping. No new credentials were provisioned here.
2. Supply a business approval/evidence provider for one actual eligible integration and immutable failed snapshot. Existing six DEV projections are not newly approved business targets; the pilot is synthetic and the other five lack attested SAP provenance.
3. Validate current physical schema/ACLs and transaction behavior with explicit authorization. In the last read-only catalog evidence, audit SELECT(worker,diagnostico) is not confirmed for the application role; these are necessary to the candidate. Do not grant or use an admin identity as a substitute. No schema migration is generated or required by this candidate.
4. Verify exclusive worker composition and scope; global/automatic initiators must not compete. Bind the existing pipeline/adapters/runtime to the approved mapping; never relax historical automatic admission guards.
5. Obtain separate deployment/physical-write/business-call authorization and a current UTC window. No such activation is included in 1.2.1.

Offline tests and static review do not prove PostgreSQL physical durability/ACID or effective future ACLs. The operational command path remains BLOCKED until these prerequisites are met. The current GUI can inspect/read only.
