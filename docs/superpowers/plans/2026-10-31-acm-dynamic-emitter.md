# ACM Dynamic Emitter Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans; execução inline, revisão independente final.
**Goal:** validar novos emissores por GET Guardian sem depender de whitelist fixa.
**Architecture:** adapter GET-only + prova scoped ligada ao snapshot; gate/preparação/pipeline compartilham prova; cutoff impede replay histórico.
**Tech Stack:** .NET10, DI scoped, HttpClient, Npgsql existente.
**Spec:** docs/superpowers/specs/2026-10-31-acm-dynamic-emitter-design.md.
## Global Constraints
Repo oficial somente; sem commit/push; preservar MM1.2.12,SD,Toledo,5432/5434; sem SOAP/REST empresarial, migração ou banco mutante. Defaultfalse; publicação separada. Token provider já existente; nenhum segredo em evidência.
## Review Focus
Código trocado/tenant errado; respostas ambíguas; prova entre snapshots; alteração de material ou journal; liberação involuntária de1175/backlog.
## Task1 — consulta
- [x] DTO/porta GuardianEmitterRead; testes RED de adapter via reflection e fake HTTP.
- [x] Adapter HTTPS/rota/assinatura/Bearer/boundedJSON/timeout, sem retry; GREEN.
## Task2 — gate e preparação
- [x] Testes RED resolver/escopo/expiração/código novo/ausente/inativo/sem execução.
- [x] Prova scoped e opt-in, readonly RecebidoEmUtc existente, normalização candidata e validação real; Gate só libera com prova.
- [x] Pipeline e comando usam async admissão; journal/fencing/REST-only recovery preservados; GREEN.
## Task3 — regressão e entrega
- [x] R158,R165U e testes relevantes PASS; build Host/Callback/WinForms sem mudança de instalação.
- [x] Revisão independente; corrigir defeitos provados com RED→GREEN.
- [x] Candidato Linux versionado, manifesto/SHA, configuração inerte, precheck/publicação/rollback; propor autorização operacional concreta sem despachar1175.
Sem commits por regra do operador; sem worktree novo por preferência canônica.
