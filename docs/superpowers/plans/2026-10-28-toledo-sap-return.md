# Guardian/Toledo → SAP automatic return and manual retry

Goal: retain the received document, automatically submit newly admitted eligible receipts using the existing motor, expose SAP return status and safe manual retry in the existing Toledo form.
User ruling: automatic first delivery; retry only after a known eligible failure. Do not add TipoOperacao now, do not fabricate any missing field. Raw original stored JSON must remain intact. No real SAP call is authorized for verification.
Architecture: typed operation for the full-document receipt, separate from the six-field callback/ticket enrichment path. Reuse motor claim/lease/fencing and authenticated SAP CSRF transport. No HTTP dispatch in the WinForms, no write command in GET-only reader. A durable queue admission must share the receipt transaction; redelivery cannot create new work or recover historical receipts. Default runtime remains disabled until a legitimate SAP authentication binding and controlled activation are verified.
Concrete blockers found: current receipt state H4_NOT_FOUND; existing mapped ACM gateway requires ticket data/TipoOperacao and cannot be weakened globally. Existing configured Toledo callback has no SAP authentication/provider or worker. SAP response contract does not establish business confirmation: 2xx is technical acceptance only and cannot authorize blind retry.

Files: new Aplicacao/Servicos/Recebimento/DevolutivaToledoSap.cs (typed policy, motor processor, ports); new Integracao.SAP/Http/DocumentoToledoSapGateway.cs (existing CSRF executor, original document); existing query DTO and Toledo Form/Designer; focused tests and this plan. Persisted readback/queue wiring will be added only where its schema and permissions are proved, without physical DDL/DML in this phase.

- [x] RED: verify missing SAP-return capability.
- [x] Core processor: original fingerprint, exact receipt scope, persisted outcome, no implicit automatic retry, no assumptions about SAP business success.
- [x] Safe retry policy, authenticated/authorized server command port and audit requirements; refuse missing or ambiguous state.
- [x] Existing form: SAP status, reason and Reenviar; preserve JSON, no first-send button, absent server path visibly blocked.
- [x] Focused tests, Release build, review; record limitations and prepare coherent candidate. Do not claim deployed automatic forwarding while operational dependencies are absent.
No commit, VM change, migration, external POST or historical replay. Preserve current DEV1101.2.5 until a coherent tested candidate and rollout scope exist.

- [ ] Controlled SAP activation remains pending: legitimate SAP credential binding and new eligible H4-correlated receipt, plus operational release. No historic receipt replay.

Ruling: FUGATES is the supplied SAP username; do not treat it as proof of password availability. Reuse the existing systemd provider only.
Ruling: no TipoOperacao field is added or inferred; original document transport was explicitly requested.
Physical reader closure: API had already been promoted to fugates_dev_read_app. Identity was PASS but Toledo guard retained the app-role literal. Corrected only the expected-role propagation; readonly catalog confirmed all needed privileges exist, so no GRANT or migration was performed. Updated GET-only reader only, preserving callback/command service PIDs.
