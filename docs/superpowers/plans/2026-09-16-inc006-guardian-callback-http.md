# INC006-C Guardian Callback HTTP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an offline-tested minimal HTTP endpoint that delegates Guardian SET26 receiving callbacks to the existing application flow.

**Architecture:** A separate ASP.NET Core host owns only transport concerns. Its endpoint maps a tolerant DTO into `CallbackGuardianMvp`; existing application and in-memory infrastructure remain authoritative for validation, correlation, idempotency, and SAP fake delivery.

**Tech Stack:** .NET 10, ASP.NET Core shared framework, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-16-inc006-guardian-callback-http-design.md`

## Global Constraints

- No real Guardian or SAP calls.
- No database write or migration.
- Do not alter INC002-005 contracts or idempotency rules.
- Do not commit or push.
- Tests invoke the HTTP adapter in memory and open no network socket.

---

### Task 1: HTTP Contract RED

**Files:**
- Create: `tests/FugaTES.Testes/Inc006GuardianCallbackHttpTests.cs`
- Modify: `tests/FugaTES.Testes/FugaTES.Testes.csproj`

**Interfaces:**
- Produces expected API: `GuardianCallbackEndpoint.HandleAsync(HttpContext)` and `GuardianCallbackHttpComposition.ConfigurarServicos(IServiceCollection)`.

- [ ] Write focal tests that send JSON through `DefaultHttpContext`, resolve the existing application flow through DI, and assert valid mapping, extra-field tolerance, required-field rejection, malformed JSON rejection, idempotency, conflict, and cancellation.
- [ ] Add the not-yet-existing project reference to the test project.
- [ ] Run `dotnet test ... --filter FullyQualifiedName~Inc006GuardianCallbackHttpTests --no-restore` and record the expected compile failure caused by the absent HTTP project/types.

### Task 2: Minimal HTTP Host GREEN

**Files:**
- Create: `src/FugaTES.Callback.Http/FugaTES.Callback.Http.csproj`
- Create: `src/FugaTES.Callback.Http/Program.cs`
- Create: `src/FugaTES.Callback.Http/GuardianCallbackHttpDto.cs`
- Create: `src/FugaTES.Callback.Http/GuardianCallbackEndpoint.cs`
- Create: `src/FugaTES.Callback.Http/GuardianCallbackHttpComposition.cs`
- Modify: `FugaTES.slnx`

**Interfaces:**
- `GuardianCallbackHttpDto` exposes nullable transport fields and maps only to `CallbackGuardianMvp`.
- `GuardianCallbackEndpoint.Map(IEndpointRouteBuilder)` maps `POST /api/guardian/set26/callback/recebimento`.
- `GuardianCallbackEndpoint.HandleAsync(HttpContext)` returns 200, 400, or 409 and propagates `RequestAborted`.
- `GuardianCallbackHttpComposition.ConfigurarServicos(IServiceCollection)` registers singleton in-memory repository, SAP fake, and scoped application use case.

- [ ] Create the Web SDK project with references only to Aplicacao and Infraestrutura.
- [ ] Implement tolerant System.Text.Json deserialization and strict MVP presence/value validation.
- [ ] Delegate to `ProcessarCallbackGuardianMvpCasoDeUso`; do not reproduce idempotency logic.
- [ ] Map application outcomes to the frozen HTTP statuses and emit only a minimal status JSON.
- [ ] Build `WebApplication`, configure services, map endpoint, and run.
- [ ] Generate local restore assets with empty remote restore sources if required.
- [ ] Run focal tests and require all to pass with zero real network calls.

### Task 3: Regression And Build Evidence

**Files:**
- Verify all files above; no additional production files are authorized.

- [ ] Run all `FugaTES.Testes` in Release with `--no-restore`; require zero failures.
- [ ] Run `dotnet build FugaTES.slnx -c Release --no-restore`; require zero warnings and errors.
- [ ] Inspect `git status --short` and changed paths without Git writes.
- [ ] Report Gate fields, including real-call counters and remaining blockers.
