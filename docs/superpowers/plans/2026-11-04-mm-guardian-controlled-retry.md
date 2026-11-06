# MM controlled retry implementation plan

> Execute inline with superpowers:executing-plans, TDD and one final independent review. No commits authorized.

Goal: recover deterministic MM masterdata failures through authenticated commands consumed by the existing sole VM worker. Keep unknown/created records locked.
Spec: docs/superpowers/specs/2026-11-04-mm-guardian-controlled-retry-design.md — approved by operator04/11.
Tech: .NET10,WinForms,Npgsql,PostgreSQL18,existingGuardianGET/POST adapters,existing18763auth.

Constraints: canonical official source only (no parallel worktree per operator); preserve dirtySD/Cadastros/readAPI changes. All physicalSQL/deploy/POST remain unexecuted until concrete operational approval. Existing data/JSONimmutable; company3000,centers3011/3008,status05,datecutoff01/11 preserved.

## Tasks
- [x] 1.RED tests: masterdata gate, eligibility, safe400diagnostics and missingUIButton. Contracts in Aplicacao/Modelos/Mm/MmManualRetry.cs and Contratos/Mm/IMmRetryStore.cs; service/gate/policy in Servicos/Mm/MmManualRetry.cs. Existing constructor/contracts remain compatible.
- [x] 2.GREEN domain: provenmissing/inactive/unverified masters preventPOST; currentsourcehash/registeredpayload/version match,knowncorrectiblefailureonly; protectedlegacy evidence for actual2991 via evidencehash,not hardcodedbusinessdefaults; authSID/justification/digest and doubleclick safety.
- [x] 3.Command/audit store: Persistencia/Mm/MmRetryPostgreSqlStore.cs; candidateSQL docs/Incrementos/007/MIGRATION_007M2_MM_RETRY.sql,validation/rollback. Onependingcommand/key,txaudit,claimtoken,markbeforePOST,atomicterminalresultwithregistry. Neverreset/delete reservation; refuseoldunknownstate.
- [x] 4.Server/worker: Callback.Http/MmRetryEndpoint.cs,ManualRetry/MmRetryCommandComposition.cs; extend existingAcmManualRetryCommandHost only opt-inMM. HostIntegracao/Mm/MmLinuxComposition.cs andMmLinuxOptions.cs: sameworkerlease consumescommands sequentially; existingproviders/readGETguards; no newservice. Sharedprotected policy and dedicatedMMdatabase wrapper avoid overridingACMconnection.
- [x] 5.UI/client: partialAuthenticatedAcmRetryCommandClient.Mm.cs; existingclient addspartial/interfaceonly. FrmPedidosCompraMm partialretryaction; per-rowdisabledrendering; confirmation,justification,sharedcommandclient bindingthroughUnifiedDesktopSession; preserveJSON/history. NoGateway/SQL inUI.
- [x] 6.Observation: safeMMblockcodes rendered fromvalidatedpersistedoutcome; read-onlyAPIremainsGET-only. PendingcommandincludedinpreviewsootherWindows/restarts cannotduplicate.
- [x] 7.FocalRelease tests: recoverable,created,HTTP400uncorrected,timeout/indeterminate,concurrency/claimloss,persistencefailure,sourcechange,tampering/authGET-only,originalbytes/hash,protocolconfirmation/audit,peritemisolation. No externalPOST or physicalDBDML.
- [x] 8.Independentreview,packageversion/hash,SQLcandidateandACLmatrix,promotion/rollbackplan. Do not install until review and operator operational approval.

## Rulings
- Existing18763authentication andtunnel are reused; newMMrouteopt-inandSIDpolicyrequired. API18762neveracceptswrites.
- Current2991legacy400bodycannotbereconstructed; allowonlyprotectedexplicit evidence referencing live404proof + matchingreservation/source/payload, then requirefreshsuccessfulGETmastersandoperatorconfirmation. Actualrealcreationnotperformedbytests/preparation.
- Masterdata check is shared between newautomaticcreations and manualrecovery; operator doesnotcreate masterdata inFugaTES.
- Eachgridrowisonepedido/item/protocol; createdsiblingsareskipped. ChangedSAPsource isblockedforreconciliation,not silently rewritten.
- Baselineunknown/terminalfirstattemptsnevergetreset. NewMMcommandtablepreservesprioroutcome; commandstart/resultmustbecommitted independently ofUIcancellation.

## Fechamento offline
04/11/2026:900PASS/0FAIL;Release runtime/Desktop;17/16arquivos;DLLsidenticas aos testados;revisão independente concluída, incluindo política systemd eACLs. DDL/GRANT/publicação/envio real NÃO executados. Referência evidence/MM_RETRY_20261104/final-r2.
