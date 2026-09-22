# INC006 Guardian HTTP SET26 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implementar clients HTTP Guardian SET26 testados integralmente com handler fake e sem rede real.

**Architecture:** Um novo projeto `FugaTES.Integracao.Guardian` isola options, credenciais, token e operacoes HTTP. Ports permanecem em Aplicacao e testes referenciam diretamente o novo projeto.

**Tech Stack:** C# 14, .NET 10, HttpClient, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-16-inc006-guardian-http-set26-design.md`

## Global Constraints

- Nenhuma rede real, callback inbound, SAP real ou migration.
- Nenhum default de URL, rota pendente ou credencial.
- Nenhum arquivo legado INC002-005 alterado.
- Sem commit ou push.

---

### Task 1: Project, options and token client

**Files:**
- Create: `src/FugaTES.Integracao.Guardian/FugaTES.Integracao.Guardian.csproj`
- Create: `src/FugaTES.Integracao.Guardian/GuardianHttpOptions.cs`
- Create: `src/FugaTES.Integracao.Guardian/GuardianTokenClient.cs`
- Modify: `src/FugaTES.Aplicacao/Contratos/Recebimento/RecebimentoMvpPorts.cs`
- Modify: `FugaTES.slnx`
- Modify: `tests/FugaTES.Testes/FugaTES.Testes.csproj`
- Test: `tests/FugaTES.Testes/Inc006GuardianHttpSet26Tests.cs`

**Interfaces:** Produces `GuardianHttpOptions`, `CredenciaisGuardianSet26`, `ICredenciaisGuardianSet26Provider` and `GuardianTokenClient.ObterTokenAsync`.

- [ ] Add RED tests for token request, missing BaseUrl, invalid token and non-success response.
- [ ] Run focal tests and verify RED.
- [ ] Implement project wiring, strict options and token client.
- [ ] Run focal tests and verify GREEN.

### Task 2: SET26 operation client

**Files:**
- Create: `src/FugaTES.Integracao.Guardian/GuardianSet26HttpClient.cs`
- Modify: `src/FugaTES.Aplicacao/Contratos/Recebimento/RecebimentoMvpPorts.cs`
- Modify test: `tests/FugaTES.Testes/Inc006GuardianHttpSet26Tests.cs`

**Interfaces:** Produces `IEmissorGuardianSet26Gateway`, `ITransportadoraGuardianSet26Gateway` and HTTP implementation of all three Guardian gateways.

- [ ] Add RED tests for Bearer, methods/routes, canonical payloads, configurable endpoints and fail-closed status/body.
- [ ] Run focal tests and verify RED.
- [ ] Implement the minimum HTTP client with timeout and cancellation.
- [ ] Run focal tests and verify GREEN.

### Task 3: Verification

**Files:** No further source changes expected.

- [ ] Run all INC006-B focal tests.
- [ ] Run full productive regression tests.
- [ ] Run Release build and require zero warnings.
- [ ] Confirm no legacy file changed and no real network call occurred.
