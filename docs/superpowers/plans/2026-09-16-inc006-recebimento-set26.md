# INC006 Recebimento SET26 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar o menor fluxo offline de Recebimento SET26 entre entrada SAP sintetica, pre-cadastro Guardian, callback, correlacao e saida SAP fake.

**Architecture:** Novos modelos e ports vivem em `FugaTES.Aplicacao`; DTOs/mapeadores e fake Guardian SET26 vivem em `FugaTES.Integracao.Toledo`; correlacao em memoria e fake SAP outbound vivem em `FugaTES.Infraestrutura`. Contratos legados permanecem intactos.

**Tech Stack:** C# 14, .NET 10, xUnit, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-09-16-inc006-recebimento-set26-design.md`

## Global Constraints

- Offline; nenhuma chamada Guardian, SAP, banco ou rede.
- Nenhum segredo, URL default ou credencial hardcoded.
- Nenhuma alteracao em contratos INC002-005.
- Sem migration e sem commit.

---

### Task 1: Contratos e mapeamento SET26

**Files:**
- Create: `src/FugaTES.Aplicacao/Modelos/Guardian/ContratosGuardianSet26.cs`
- Create: `src/FugaTES.Aplicacao/Modelos/Recebimento/RecebimentoSapCpiMvp.cs`
- Create: `src/FugaTES.Integracao.Toledo/Set26/MapeadorGuardianSet26.cs`
- Test: `tests/FugaTES.Testes/Inc006RecebimentoMvpSet26Tests.cs`

**Interfaces:** Produces SET26 emissor, transportadora, pre-cadastro, SAP input and DTO mappings.

- [ ] Write tests for canonical fields, fields 1000/1002/1003 and absence of legacy wire values.
- [ ] Run focal tests and verify RED.
- [ ] Implement the minimum models and mapper.
- [ ] Run focal tests and verify GREEN.

### Task 2: Ports, correlation and initial receipt

**Files:**
- Create: `src/FugaTES.Aplicacao/Contratos/Recebimento/RecebimentoMvpPorts.cs`
- Create: `src/FugaTES.Aplicacao/CasosDeUso/Recebimento/ProcessarRecebimentoMvpCasoDeUso.cs`
- Create: `src/FugaTES.Infraestrutura/Recebimento/RepositorioCorrelacaoRecebimentoMemoria.cs`
- Create: `src/FugaTES.Integracao.Toledo/Set26/PreCadastroGuardianSet26GatewayFake.cs`
- Modify test: `tests/FugaTES.Testes/Inc006RecebimentoMvpSet26Tests.cs`

**Interfaces:** Produces Guardian gateway, correlation repository, authentication port, protocol and correlation records.

- [ ] Write tests for valid input, required-field rejection and document-to-protocol correlation.
- [ ] Run focal tests and verify RED.
- [ ] Implement ports, fake gateway, in-memory repository and initial use case.
- [ ] Run focal tests and verify GREEN.

### Task 3: Callback and SAP outbound

**Files:**
- Create: `src/FugaTES.Aplicacao/Modelos/Recebimento/PesagemSapMvp.cs`
- Create: `src/FugaTES.Aplicacao/CasosDeUso/Recebimento/ProcessarCallbackGuardianMvpCasoDeUso.cs`
- Create: `src/FugaTES.Infraestrutura/Recebimento/PesagemSapMvpGatewayFake.cs`
- Modify test: `tests/FugaTES.Testes/Inc006RecebimentoMvpSet26Tests.cs`

**Interfaces:** Produces callback model, SAP weighing model, idempotent callback result and SAP outbound port.

- [ ] Write tests for ticket/weights, SAP output, idempotent duplicate and conflicting duplicate.
- [ ] Run focal tests and verify RED.
- [ ] Implement callback use case and fake outbound.
- [ ] Run focal tests and verify GREEN.

### Task 4: Verification

**Files:** No source changes expected.

- [ ] Run all INC006 focal tests.
- [ ] Run full productive regression tests.
- [ ] Run Release build with zero warnings.
- [ ] Inspect changed/added files and confirm no legacy INC002-005 file changed.
