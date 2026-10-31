# Cadeia SAP → FugaTES → Guardian: estado em 29/10/2026

## O que funciona, provado com contrato real

O contrato **0000001160** foi emitido pelo SAP às 10:53 e percorreu sozinho:

```
SAP → nginx (URL inalterada) → receptor 8080 → COMMIT em fugates_dev:5433 → visível no DEV110
                                                      ↓
                                      motor capturou e tentou despachar (11:04 e 11:54)
```

Nada disso exigiu mudança no SAP. A URL corporativa continua a mesma.

### Três defeitos corrigidos no caminho

1. **Banco errado.** Existem duas bases `fugates_dev`, em clusters diferentes. O receptor gravava
   na legada (5432); o DEV110 lê a unificada (5433). Era a razão de o contrato chegar com
   `HTTP 201` e nunca aparecer. Corrigido no `runtime.env` (porta e role).
2. **Privilégios incompletos** da role de escrita. Faltavam `SELECT` em `integracao` — só 8 colunas
   estavam liberadas, sem `estado` —, `SELECT` em `pausa_processamento` e `SELECT, INSERT` em
   `tentativa_integracao`. Delta `008`.
3. **`search_path` ausente.** A role não o tinha e a connection string não o passava; nenhuma
   tabela resolvia. Corrigido com `ALTER ROLE ... SET search_path = fugates, pg_catalog`.

## O que bloqueia, e é por desenho

O motor recusa **antes de qualquer chamada de rede** — 130 ms, enquanto só o token do Guardian leva
161 ms. A recusa vem de `AcmR57NormalPreparation.Bloqueio()`:

```csharp
!Enabled || !ExecutionAuthorized || !Proven(AuthorizationReference)   → R165U_EXECUTION_DISABLED
GuardianEnvironment != "HML" || !Proven(GuardianIdentityEvidence)     → R165U_GUARDIAN_IDENTITY_REQUIRED
!Proven(PostgreSqlIdentityEvidence) || !Proven(JournalEvidence)       → R165U_DURABLE_READINESS_REQUIRED
```

`Proven()` recusa vazio, nulo e os literais `PENDENTE`, `NOT_APPROVED`, `NAO VALIDADO`.

**Faltam quatro referências, todas da Hera.** Não são flags técnicas: são atestados de autorização e
de verificação, e preenchê-los com texto arbitrário seria falsificar governança.

| Chave | O que atesta |
|---|---|
| `AcmR57:Normal:AuthorizationReference` | autorização de execução |
| `AcmR57:Normal:GuardianIdentityEvidence` | verificação da identidade Guardian |
| `AcmR57:Normal:PostgreSqlIdentityEvidence` | verificação da identidade PostgreSQL |
| `AcmR57:Normal:JournalEvidence` | verificação do journal |

## O alvo correto do cadastro de contrato

O cadastro **não vai para a nuvem**. Vai para um serviço SOAP interno, e o produto já o conhece:

```csharp
if (uri.Scheme=="http" && (!ContratoSoapInternalHmlHttpApproved || ContratoSoapEnvironment!="HML"
    || uri.Host!="192.168.17.91" || uri.Port!=80))
    throw new GuardianHttpException("Guardian internal HTTP SOAP profile is not approved.");
```

Validado por POST manual em 29/10: `CadastraContrato` respondeu `Erro=0`,
"Contrato cadastrado com sucesso!", `Sequencial 587`. E a VM alcança o serviço (`HTTP 200`).

Configurado no `motor.env`:

```
Guardian__ContratoSoapEndpoint=http://192.168.17.91:80/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx
Guardian__ContratoSoapEnvironment=HML
Guardian__ContratoSoapInternalHmlHttpApproved=true
```

A REST em `guardian.cloudprix.com.br` atende outra etapa, o protocolo de pré-cadastro. Credencial
validada: `CreateToken` devolveu `200` com `succeeded`/`token`.

### Correção de uma leitura minha

Afirmei que não havia caminho no código para gravar no Guardian produtivo e que
`GuardianEnvironment=HML` contradizia o host da nuvem. O enquadramento estava errado: o cadastro de
contrato nunca foi para a nuvem, e `HML` é o rótulo correto do perfil SOAP interno. O teste manual
do operador foi o que esclareceu — eu investigava o alvo errado.

## Lacuna do produto que custou caro hoje

**O motor não registra o motivo do bloqueio.** `Bloqueio()` devolve um código preciso
(`R165U_EXECUTION_DISABLED` e afins) e ele se perde: `auditoria_integracao.diagnostico` fica vazio
nos cinco eventos, e nem em `Logging__LogLevel__Default=Debug` o pipeline o emite.

Diagnosticar levou horas de inspeção de código. Gravar esse código em `diagnostico` responderia em
trinta segundos. **Recomendação à Hera:** persistir o código de bloqueio e, quando houver resposta
HTTP do Guardian, o status e a mensagem.

## Estado operacional

| | |
|---|---|
| `fugates-callback-http` | **active**, gravando em `fugates_dev:5433` |
| `fugates-acm-motor-dev` | **instalado e parado** — sobe apenas com escopo definido |
| Contrato 1160 | no banco, `AGUARDANDO_REPROCESSAMENTO`, retry agendado |
| Endpoint SOAP | configurado e alcançável da VM |
| Credencial REST | válida |

Quando as quatro referências chegarem: preencher no `motor.env`, `instalar`, e
`ligar 1160 -`. O contrato segue para o Guardian sozinho.
