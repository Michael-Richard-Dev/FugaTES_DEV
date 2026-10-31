# Toledo date formats Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Inline; one fresh final review. No commits or worktrees by user instruction.
**Goal:** aceitar data brasileira24h e preservar US AM/PM, com mesmo instante SAP.
**Architecture:** somente formato adicional em TryParseExact de dois parsers atuais.
**Tech Stack:** .NET10, System.Globalization, xUnit existente.
**Spec:** docs/superpowers/specs/2026-10-31-toledo-date-formats-design.md
## Global Constraints
Repo canônico; preservar dirtyMM/SD/ACM; sem POST/DBwrites/deploy sem aprovação específica; sem commit/push.
## Review Focus
Dia/mês ambíguo;12AM/12PM; meia-noite; fusoBrasília; datas inválidas; JSON original; parser da prévia JSON SAP.
## Task1 — formato explícito em duas camadas
Files: src/FugaTES.Callback.Http/ToledoPesagemCallbackDto.cs; src/FugaTES.Integracao.SAP/Http/ToledoSapODataPayload.cs; tests/R158.SapInboundReadiness.Tests/ToledoDateFormatsTests.cs.
- [x] Testes RED de ingresso/projeção/cultura/ambiguidade/horário/invalidade, sem rede.
- [x] Acrescentar dd/MM/yyyy HH:mm:ss nas duas listas, sem outras mudanças; GREEN.
- [x] BuildRelease e regressão Toledo aplicável; revisão independente.
- [x] Preparar patch mínimo contra runtime Toledo congelado; validar binários exatos, manifesto/SHA, promoção/rollback. API GET-only recebe DLL SAP para leitura do JSON. Não alterar motor ACM recém-publicado.
