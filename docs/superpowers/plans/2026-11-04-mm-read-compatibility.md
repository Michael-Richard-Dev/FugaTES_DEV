# MM read compatibility repair

Implementation plan executed inline using systematic-debugging and test-driven-development.

Goal: restore MM read compatibility without changing the worker or business records; isolate MM observation errors from database connectivity.

Constraints: official source only; preserve dirty SD/Cadastros changes; no commit, POST, DML, migration or motor restart. Cadastros publication remains paused.

- [x] Reproduce old reader failure: JsonException at $.dadosProtocoloPreCadastro.numeroReferencia, not DataCriacao. Compare exact installed assembly hash.
- [x] Prove existing approved motor Aplicacao DLL repairs old reader validation with actual snapshot in memory, no payload export.
- [x] RED: API scoped safe failure response, client isolation, wrong identity/auth still rejected, localized MM message.
- [x] GREEN: modify only DevUnifiedReadApi, PhysicalMonitorClient, PhysicalReadConnection and FrmPedidosCompraMm.
- [x] Focal tests and exact runtime validation. Preserve unknown-field rejection and historical documents without OC.
- [x] Prepare minimal one-DLL reader package, hashes, prechecks and rollback. No motor publication.
- [x] Report installation scope and any remaining local publication dependency without silently publishing Cadastros.

## Closure
- Operator explicitly authorized reader-only publication (package093B5A09CA68...). Installed in versioned private reader folder; exact one-DLL change, SHA/backup verified.
- Live GETidentity/contracts/monitor/toledo/mm all200,5433READONLY,3currentitems,169records,fresh. MMmotorPID3124 preserved.
- Source code:18newfailure-isolationcases within63focalPASS;26old-releaseexacttestsPASS;295monitorregressionPASS.
- ReviewP2 fixed with RED/GREEN; public one-string exception constructor preserved. Old startup test expectation1.2.11 updated to existing1.2.17candidate; no release bump.
- Scoped diagnostics/UI/source enhancements are tested but NOT installed by this reader-only authorization. LocalCadastros1.2.17publication remains paused. Localtunnel absent during diagnosis; noDesktopvisualclaimmade.
- NoPOST,DML,migration,GRANT,credentialschange,commit,push ormotorrestart.
