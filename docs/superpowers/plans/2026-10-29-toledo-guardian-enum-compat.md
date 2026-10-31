# Guardian Toledo wire compatibility implementation plan

Goal: accept the Guardian E/S callback and prepare the demonstrated SAP representation without modifying the original document or replaying existing receipts.
Architecture: keep raw callback text, fingerprint and identity unchanged; validate direction at ingress and normalize only the outbound OData projection. Reuse the existing AcmSapWeighingDirection evidence (E/1, S/2). Quality aliases 1/IMPUREZA and 2/UMIDADE come from the two user-supplied payloads. Preserve all measured values and current callback/auth/window/lease/fencing guards.
Tech stack: canonical .NET10 solution, existing callback, SAP adapter and xUnit projects. No new workspace, provider, database schema or service. No commit/push, SAP POST or replay of0015135/20318.

Files: modify src/FugaTES.Callback.Http/ToledoPesagemCallbackDto.cs and src/FugaTES.Integracao.SAP/Http/ToledoSapODataPayload.cs. Add tests/R158.SapInboundReadiness.Tests/ToledoGuardianWireCompatibilityTests.cs. Existing unrelated changes and Claude source stay intact.

- [x] Add failing tests for ingress E/S retention; outbound E/S and numeric compatibility; quality aliases and retained zero; alias duplicate rejection; unknown direction; original JSON/hash/leading zeros preserved; fake transport body.
- [x] Run focused RED, implement minimal correction and preserve the established audited wire format version V1, then run GREEN.
- [x] Run pertinent existing callback/storage/OData/return/retry regressions and Release builds without any external transport.
- [x] Review diff, package immutable binaries and manifest/hash; preserve current runtime and protected configuration/window in deployment/rollback preparation. No real POST from assistant.
- [x] Publish only a reviewed compatible candidate under the authorized operational scope; restart requires existing legitimate administrative terminal. Do not extend the current window or replay backlog. If additional authorization is required, present exact package and operation after completing the preparation.

Verified outcome:332 focused source regressions PASS;141 tests of final exact R3 binary bundle PASS; inert startup RED/GREEN reproduced dependency mismatch and confirmed correction; inert Linux probe PASS before restart; final installed17-file hash readback and health18086/8080 PASS. Only2DLLs changed;15 baseline files byte-identical. AssemblyVersion1.0.0 retained to match frozen runtime, FileVersion1.2.7. Initial failed promotion rolled back without database restore; corrected package A8EEB6876742462A23F8AE176F24060377B6E1E93767D5E86A4C8B1814CBDB77 installed under explicit operator authorization. No POST by installer, replay, schema/GRANT or Claude source change. Current authorization window unchanged. Evidence: TOLEDO_GUARDIAN_ENUM_COMPAT_20261029/FINAL_RESULT.json.
