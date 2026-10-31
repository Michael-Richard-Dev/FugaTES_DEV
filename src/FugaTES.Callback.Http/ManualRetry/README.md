# Dedicated ACM manual retry command host

Start exactly `FugaTES.Callback.Http --acm-manual-retry-command` on the Linux VM. This exclusive flag rejects additional arguments, including the read API flag. Kestrel binds only `127.0.0.1:18763`. It maps the existing authenticated manual-retry GET preview / POST routes and authenticated `GET /health`; it never maps callbacks, SAP inbound, Guardian protocol commands, or the read API.

Set only `FUGATES_ACM_MANUAL_RETRY_PROFILE` to the absolute private profile JSON path. Profile keys are case-sensitive camelCase and unknown keys are rejected:

```json
{
  "baseConfigurationFile": "/opt/fugates/releases/FugaTES.ACM/R165AD_SAFE_ERROR_READER_20261025_R2/runtime-http/appsettings.json",
  "credentialFile": "/etc/fugates/acm-manual-retry/command.key",
  "approvalsFile": "/etc/fugates/acm-manual-retry/approvals.json",
  "passfile": "/home/michael/.fugates-acm-r165p/pgpass",
  "operatorSid": "S-1-5-21-1842992099-521793823-1132869621-1001",
  "workerIdentity": "acm-manual-retry-dedicated"
}
```

The initial approvals file is exactly `[]`. Do not add a fabricated target, scope, snapshot digest, business evidence or authorization window. With an empty list all authenticated previews and confirmed requests are blocked before store access; the motor, Npgsql data source and dedicated hosted worker are not composed. Health reports zero approvals. No database/business readiness is implied by this process health.

The credential file contains a dedicated random 64-character hexadecimal Bearer credential (optional final CR/LF). It must be independent of the read API key and Guardian credentials. The server maps it to `operatorSid`; request headers/body cannot choose the actor. Profile, credential, approval and optional normal configuration files must be outside source/release/current directory, regular files with mode 0600 or 0400, owned by root or the effective service UID. All ancestors must be root/service-owned and not group/other-writable. Symlinks are rejected throughout. The existing base configuration is integrity-checked without requiring private read mode. No credential is stored in this example or a package.

For a future proven approval, add explicit `contract`, `item` and `normalConfigurationFile` to the profile, and exactly one server-owned `AcmRetryApproval` record to the approvals array. Its JSON uses the existing record's camelCase keys and `allowedStage` string `SoapThenRest` or `RestOnly`. SID, integration, contract/item, fingerprint, observed snapshot digest, stage, business evidence, authorization reference and finite start/end UTC window must come from separately verified evidence. A REST-only record also requires the exact original contract mapping hash. The existing policy and PostgreSQL store still re-evaluate state, canonical H4, snapshot, stage, unknown outcomes and corrected failure evidence transactionally. A second approval or any scope/SID mismatch fails startup.

The optional normal configuration file is a complete camelCase `AcmR57NormalOptions` JSON object for that proven target, including existing Guardian/PostgreSQL/journal identity evidence, approved partner/material mappings, mapping evidence, proximity approval and callback binding. It is never inferred from legacy mappings. Partner/material mappings, when provided, must match the explicit contract/item and centre 3011. Inbound-window and window-mapping modes are rejected. Only after validating the one static approval, the dedicated process overrides `Enabled`, `ExecutionAuthorized` and `AuthorizationReference` in memory for these options. The source JSON remains unchanged. Original normal-pipeline preflights and adapters remain authoritative; no evidence/mapping/callback validation is bypassed.

The existing base configuration supplies original runtime timeouts and adapter settings. Only `Guardian__Email`, `Guardian__Password` and `Guardian__Signature` may be overlaid from the legitimate Guardian EnvironmentFile for original adapters. They do not authenticate this host. In-memory motor configuration fixes PostgreSQL to `127.0.0.1:5433/fugates_dev`, search path `fugates`, role `fugates_acm_hml_app`, explicit external Passfile, no inline password and `Enlist=false`; disables the global worker/inbound window; and replaces any inherited pilot with only the profile's exact contract/item/3011. Before composing Npgsql for a nonempty approval, a metadata-only guard validates the existing Passfile as a regular external file of 1–65536 bytes, mode 0400/0600, owner root/effective UID, with no symlink or group/other-writable ancestor. This guard never opens or reads its contents. Empty approvals do not inspect the provider. Original `NpgsqlDataSourceFactory.CriarAcm` physical identity guards remain active. Environment/appsettings cannot override the HTTP listener.

The dedicated queue receives only matching `QUEUED` receipts returned after the existing store's admission COMMIT. `BLOCKED`, `UNKNOWN`, `ALREADY_RECORDED`, exceptions and mismatched receipts never schedule a worker. The queue is bounded to one pending wake; a wake failure is reported as unknown, without automatic recovery. One scoped original `AcmManualRetryWorker.ExecuteApprovedAsync` executes the exact admitted command using the same fixed identity as `MotorIntegracaoServico`. It never polls a persisted queue, runs the corporate cycle, recovers backlog, retries after an unknown outcome, or resumes queued commands automatically after restart. REST-only recovery and all unknown-outcome blocks remain in the original code.

This source change performs no migrations/grants, provisioning, service installation, real POST or historical replay. Those operations require their separately authorized deployment procedure.

