# Toledo private DEV without application key

Goal: accept the existing Toledo JSON without a custom key, solely through the installed private DEV ingress.
Design selected by user: remove Toledo key authentication. Keep Nginx allowlist and deny-all, 127.0.0.1:18086 only, strict payload validation and PostgreSQL 5433 COMMIT-before-ACK. Preserve the default shared-secret admission for every other configuration. No other application or schema changes.

Files: ToledoCallbackAdmission.cs; new ToledoPrivateDevAdmissionTests.cs; versioned service configuration and deployment script outside source secrets.

- [ ] RED: parsed explicit PrivateDevNetworkOnly mode admits without secret only on approved DEV loopback port18086. Missing/invalid approvals, wrong addresses/port/environment and forged forwarded headers are rejected.
- [ ] GREEN: implement narrowly scoped mode; record PRIVATE_NGINX_ALLOWLIST_DEV, not cryptographic sender identity. Existing key mode unchanged.
- [ ] Run targeted tests and Toledo/legacy callback regressions, then publish full versioned receiver with hashes. No commit.
- [ ] Back up existing Toledo unit/config, verify its current hash/listener and Nginx allowlist without altering Nginx. Verify same5433 and preserve records.
- [ ] Replace only the isolated Toledo unit's release/config binding; restart only this unit, health/read-only verification and rollback on failure. No business POST, migration or changes to8080/5432/5434.

Rollback: restore original isolated unit and restart only it. Do not touch persisted receipts or the original Nginx/SAP/MasterData services.
