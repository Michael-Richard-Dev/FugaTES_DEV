# Shared DEV publication coordination

User gate: FUGATES-UNIFIED-POSTGRESQL-DEV, 27 October 2026.

Codex is preparing PostgreSQL reconciliation and the official DEV110 update in this same repository. No main merge or push is authorized. Claude Code and Codex share the source and installation; neither should overwrite an unreconciled change.

Target: `18/fugatesacmdev:5433 / fugates_dev`. Source MasterData `18/fugatesmdhml:5434` remains read-only. Installed HML datasource/service bindings must not be changed.

Current stage: read-only catalog and contract-lineage inventory; no migration, data copy or application publication yet.

Publication protocol: preserve a full DEV110 backup; record source and installed-file hashes; recheck all affected hashes immediately before writing; stop on any concurrent drift; use a dedicated publication lease in the evidence receipt. A file notice does not constitute Claude acknowledgement. No acknowledgement has been observed yet.

Evidence: `C:\Users\TI\Documents\ChatGPT\FugaTES\evidence\UNIFIED_POSTGRESQL_DEV_20261027`.
