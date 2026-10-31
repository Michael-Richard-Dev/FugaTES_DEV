# Toledo received payload monitor — implementation plan
Goal: official DEV110 menu displays real persisted Toledo/Guardian inbound receipts and original stored JSON, read-only.
Approved intent: user's request to add a screen and JSON button. Existing contracts/protocol screens and Claude commit are preserved; no business dispatch.
Architecture: extend the existing authenticated GET-only DEV reader18762, reuse its private Windows tunnel18761 and credential. Add application read DTOs/interface, bounded PostgreSQL READ ONLY repository with ROLLBACK, client adapter and WinForms Form/Designer. No new service/database or direct PostgreSQL Windows access.
UI: Integrações → Recebimentos Guardian / Toledo; recent100 receipts, filter exact contract/pesagem/receipt, time, center, persisted state, H4 correlation, JSON on demand. JSONB content is the persisted source document, not reconstructed wire bytes. No raw personal data in listing/log/evidence.
Files modified: DevUnifiedReadApi.cs; DevUnifiedReadStore.cs; PhysicalMonitorClient.cs; UnifiedDesktopSession.cs; FrmIntegracoes.cs/Designer; PainelInicialForm.cs; WinForms csproj version1.2.5; monitor test csproj.
Files created: Aplicacao/Consultas/RecebimentosToledo/RecebimentosToledoConsulta.cs; Persistencia/Consultas/RecebimentosToledo/RecebimentosToledoPostgreSqlReadRepository.cs; WinForms/Toledo/FrmRecebimentosToledo.cs/Designer and FrmRecebimentoToledoJson.cs/Designer; focused API/UI tests.
- [ ] RED: two GET resource tests and menu-entry test fail against baseline.
- [ ] Implement bounded list and exact receipt JSON reads; validate filters/GUIDs and database5433 identity; no writes.
- [ ] Implement client/interface, screen, filters, JSON viewer and menu/composition. Cancel reads on close; show failure instead of fake empty/demo data.
- [ ] Run core/API/UI focused tests, release build and code review.
- [ ] Prepare physical five-column SELECT proposal and versioned API update with rollback; execute only with explicit approval.
- [ ] Back up DEV110; publish all binary dependencies coherently; preserve local configuration/keys and Claude commit. Open official EXE and visually validate actual receipt and JSON without exporting personal fields.
Rollback: restore versioned reader and local Desktop backup; never delete/alter receipt records. No POST or replay.
