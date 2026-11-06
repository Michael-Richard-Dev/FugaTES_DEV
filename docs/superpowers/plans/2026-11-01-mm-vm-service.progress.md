# SDD ledger — plan: docs/superpowers/plans/2026-11-01-mm-vm-service.md
Spec R2 approved; direct implementation explicitly authorized.
Execution: native in canonical repository; no commits/worktrees per operator. Preserve unrelated pending files.
Pre-flight: Tasks1→2→3→4 share snapshot/coordinator; one portable implementation with thin compatibility wrappers. Task2 uses existing registry and needs no schema change. Task3 API identity and file provenance remain distinct. Task5 ships only reviewed source.
Task1: in progress. Metadata evidence confirms PurchaseOrder.CreationDate=Edm.Date; no new network call required.

Task1: complete — admission RED7/9; shared coordinator and scope GREEN139/139(core-host.trx). Fixed Windows UnixCreateMode null setter failure; Linux0600 preserved.
Task2: core implemented — inert isolated mode/strictidentity/exactsystemd scope included in139PASS. Lease loss/restart/drain tests pending.
Task3: complete — private API7/7PASS(private-api.trx), including readonly identity, missing key, stale provenance and POST405; no installed API touched.
Task4: in progress — red compile confirms missing remote constructor/profile guard; implementing async readback and no local fallback.

Task4 initial consolidated result199PASS/1FAIL: old fixture called3-minute-old data stale, incompatible with approved180s interval; updated stale fixture to5minutes. New remote UI tests passed. Added safety coverage for lock loss before/after reserve,166occupiedkeys across restart, cancellation after response and inert host startup.

Tasks1–4: focused suite209/209PASS(mm-safety.trx). No external business requests, database connections or runtime changes.
Task5: preparing unit/config/procedure/package. Cutover implementation detail: old Windows collector cannot drain via profile; procedure holds SHARE lock on MM table only when zero uncertain rows before stopping it. Any uncertainty releases lock and blocks migration. No SQL executed.

Review:3Important,0Critical,0Minor. Fix pass: independent45s delivery deadline +15s closure +15s readback, strict allowlisted snapshot documents/hash/scope/time and safe messages, root:michael0640 config/0750directory+ExecStartPre read check. review-red7FAIL; review-http-red8FAIL before fixes. Declined:physical systemd/credentials/lease and final package not run in offline review; will be required during authorized installation, not claimed as proven now.

Fix verification419PASS/1fixturefailure: writer now rejects foreign VM host before reader, so adversarial fixture uses raw untrusted file instead of trusted writer. No production regression. Added operational metadata/readability precheck (not executed).

Focal419PASS/1timingfailure: fake source failed its second20ms poll before consumer could start, correctly revoking admission; test incorrectly assumed consumer had already started. Made fake failure contingent on explicit sender-start signal and waited for observable failure state. Product concurrency logic unchanged. Review regression cases all passed in this run.

Coordination: operator confirmed Claude paused. Preserved last SD decimal delta and rebuilt all3products. Concrete shared-publication compatibility gap found before packaging: CreateMm unconditionally selected VM even without cutover profile; testRED1/2. Fixed selection to explicit VmObserver profile; missing profile preserves configured legacy Windows mode, malformed/failedVMbinding never falls back. Only Desktop sources changed; Linux host/reader binaries remain source-identical and do not need another rebuild.

Tasks1–5: COMPLETE OFFLINE. Final source438/438PASS; exactDesktop437/437PASS; exactLinuxManaged437/437PASS; provider53/53PASS; legacyHost+SD50/50PASS. Both platform Host DLL variants hash-verified in exact harness. Single source-inspection test only ran in repository, not mirrored into a second source tree. All3products Release PASS; source set780files stable. ZIP CRC and all manifest hashes PASS. No deployment, SQL execution, secrets copied, business POST or commit/push/merge. Package C:\Users\TI\Documents\ChatGPT\FugaTES\evidence\MM_VM_SERVICE_20261101\FUGATES_MM_VM_1_2_14_OFFLINE_20261101_R1.zip SHA256 A06E42D6ADEA453B5333EDA1D107368E4A8D85A0CA4A29F881DB1597C32BA975. Source reservation released to Claude after notification; operational cutover requires separate authorization and recheck if SD publication changes installed bindings.
