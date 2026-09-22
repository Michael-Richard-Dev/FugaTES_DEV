# INC006-C Guardian Callback HTTP Minimum

## Objective

Add the smallest local HTTP receiver that maps a Guardian SET26 receiving callback into the existing `ProcessarCallbackGuardianMvpCasoDeUso`. The implementation performs no real Guardian, SAP, or database operation.

## Architecture

Create a separate ASP.NET Core project, `FugaTES.Callback.Http`, instead of changing the existing worker host. The project references `FugaTES.Aplicacao` and `FugaTES.Infraestrutura`, registers the existing in-memory correlation repository and SAP MVP fake, and exposes one route:

`POST /api/guardian/set26/callback/recebimento`

The endpoint is a thin HTTP adapter. It owns JSON parsing, minimum transport validation, HTTP status mapping, and cancellation propagation. Functional correlation, idempotency, conflict detection, and `PesagemSapMvp` construction remain exclusively in the existing application flow.

## Wire Contract

The request accepts these case-insensitive JSON properties and ignores additional properties:

- `protocoloGuardian`: required nonblank string used for correlation.
- `sequencial`: required nonblank string.
- `pesoBruto`: required nonnegative decimal.
- `tara`: required nonnegative decimal.
- `pesoLiquido`: required nonnegative decimal.
- `estado`: required nonblank string.

Weight properties are nullable in the transport DTO so an absent property cannot be mistaken for a valid zero. The adapter maps them to the existing `CallbackGuardianMvp` without adding VO_TICKET fields.

## HTTP Results

- `200 OK`: application result `Processado` or `Idempotente`, returned as a minimal JSON status.
- `400 Bad Request`: malformed JSON, absent required MVP field, or invalid MVP value.
- `409 Conflict`: conflicting duplicate or missing callback correlation reported by the existing application flow.
- Request cancellation propagates from `HttpContext.RequestAborted`; it is not converted into a success or retried.

No inbound authentication, Swagger, PostgreSQL, migration, or real outbound adapter is added.

## Hosting And Tests

`Program.cs` creates the minimal web application and calls a focused composition extension. Endpoint logic is independently callable with `HttpContext`, allowing offline tests against the actual JSON body, response status/body, DI-resolved application service, in-memory repository, and SAP fake without opening a socket or adding a test-server package.

TDD coverage proves valid mapping, extra-field tolerance, missing field and malformed JSON rejection, identical duplicate idempotency, conflicting duplicate rejection, cancellation propagation, and zero real outbound calls.
