# DEV110 consolidated menu and physical protocol readback

User requests preserve b30de48, expose its Protocolos de Pré-Cadastro screen, retain contract JSON/retry, and authorize real retry. Work is canonical-source only; no commit/push, external POST, DB write, VM/service change.

Implementation: add a factory-driven menu entry, inject UnifiedDesktopSession.CreateProtocols, and reuse the existing physical GET-only client and Claude mapping. No direct datasource or credential fallback. Existing Claude files stay unchanged. Add focused UI/read-adapter tests; validate menu, filters, both JSON/retry columns and physical rows in the actual executable. Version 1.2.2, backup 1.2.1, curated binary publication, no profile/key changes.

Real retry authorization is acknowledged. Operational command binding is missing: ReadOnlyAcmManualRetryClient is installed; AcmManualRetryEndpoint is unmapped; no approved authenticated command provider/worker is configured. Do not pretend an enabled visual button establishes a safe backend. Complete the consolidated menu and report the exact technical prerequisite without removing identity/idempotency/unknown-outcome guards.

- [x] RED menu/factory coverage.
- [x] Integrate read adapter and menu, with no change to b30de48 files.
- [x] Focal protocol/contract/read-only regressions and Release build.
- [x] Backup, hash fence, publish and actual visual check of both screens.
- [x] Report real command blocker and preserve operator authorization.

Verified: 1.2.2 installed, official process/readback and both screens exercised. 90 focal cases pass. Real command activation remains incomplete; operator authorization acknowledged, backend binding missing. Rendered runtime window evidence is available; desktop pixel captures returned wallpaper and are not treated as screenshot proof.
