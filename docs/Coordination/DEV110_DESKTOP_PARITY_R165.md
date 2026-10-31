# R165 — Paridade do executável Desktop DEV110

Gate `FUGATES-DEV110-LATEST-DESKTOP-RELEASE`. Baseline instalada **1.2.4**.
`REAL_POSTS=0` · nenhuma alteração em 18762, 18763 ou 8080 · nenhum DDL, GRANT, worker ou janela.

**Conclusão: o executável instalado já está na versão mais atual das funcionalidades Desktop
concluídas. Nenhuma publicação foi feita — não há delta de Desktop a publicar, e republicar
agora levaria código Guardian inacabado do Codex para dentro do EXE oficial.**

---

## Etapa 1 — Instalado × fonte

| | |
|---|---|
| Executável | `C:\FugaTES_DEV110\FugaTES.exe` |
| Data | 28/10/2026 09:56:05 |
| Versão | `1.2.4.0` · produto `1.2.4+shared.protocol.rest.retry.20261028.b30de487e7f9ea13cf829ef353cba932db455f1c` |
| SHA-256 | `C128EFCAAE6C0E2787013523218E6C8B5DE59B340BF0B8E5F78597B86BCD3D4B` |
| Fonte | `UnifiedDesktopSession.Version = "1.2.4"`, HEAD `b30de48` — **o mesmo commit embutido no binário** |

### Alterações de Desktop posteriores à publicação: **nenhuma**

```
find src/FugaTES.WinForms -type f -newermt "2026-10-28 09:56"   →   0 arquivos
```

Zero arquivos de **qualquer tipo** (`.cs`, `.csproj`, `.resx`, `.json`) no projeto do Desktop
mudaram desde a publicação. `DESKTOP_SOURCE_PARITY=PASS`, `NEW_DESKTOP_CHANGES=NO`.

### O que mudou no `src/` desde então, e a quem pertence

| Arquivo | Autor | Projeto | Referenciado pelo Desktop? |
|---|---|---|---|
| `Callback.Http/DevUnifiedReadApi.cs` | Claude (R164) | Callback.Http | **não** |
| `Callback.Http/ManualRetry/AcmManualRetryCommandHost.cs` | Claude (R163) | Callback.Http | **não** |
| `Callback.Http/ManualRetry/AcmManualRetryCommandProfile.cs` | Claude (R163) | Callback.Http | **não** |
| `Callback.Http/FugaTES.Callback.Http.csproj` | Claude (R159B) | Callback.Http | **não** |
| `Persistencia/MotorIntegracao/NpgsqlDataSourceFactory.cs` | Claude (R162/R164) | Persistencia | sim (linkado) |
| `Persistencia/MotorIntegracao/AcmPostgreSqlIdentityGuard.cs` | Claude (R162) | Persistencia | sim (linkado) |
| `Aplicacao/Servicos/Mvp/AcmR57Pipeline.Normal.cs` | **Codex** | Aplicacao | sim (linkado) |

O Desktop referencia `Aplicacao`, `Persistencia`, `Infraestrutura` e `HostIntegracao` —
**não** referencia `Callback.Http`. Portanto as três alterações de maior volume (R163/R164) são
exclusivamente de serviço backend e **não justificam republicar o EXE**.

As duas alterações em `Persistencia` são de semântica de backend: acrescentam a role GET-only à
lista de identidades guardadas e parametrizam a role esperada na sonda de identidade física. O
código do Desktop **não referencia** `NpgsqlDataSourceFactory` nem `AcmPostgreSqlIdentityGuard`
(verificado por varredura em `src/FugaTES.WinForms`): seu caminho unificado lê pela API privada
e o caminho legado usa `NpgsqlDataSource.Create` direto.

### Build comparativo

Compilei o Desktop do fonte atual e comparei com o instalado:

```
FugaTES.exe                 IDENTICO
FugaTES.Dominio.dll         IDENTICO
FugaTES.dll                 DIFERE
FugaTES.Aplicacao.dll       DIFERE      ← contém a alteração em voo do Codex
FugaTES.Persistencia.dll    DIFERE      ← contém as alterações de backend do Claude
FugaTES.HostIntegracao.dll  DIFERE
FugaTES.Infraestrutura.dll  DIFERE
FugaTES.Integracao.*.dll    DIFEREM
```

`FugaTES.exe` e `Dominio.dll` saem **byte a byte idênticos**, o que confirma que o build é
determinístico e que o carimbo de commit não mudou. `FugaTES.dll` difere mesmo sem uma linha de
fonte alterada porque é recompilado contra assemblies que mudaram — diferença de identidade
binária, não de comportamento.

## Etapa 3 — Conflito concreto com o Codex

`CODEX_CONFLICTS=DETAILS`

`src/FugaTES.Aplicacao/Servicos/Mvp/AcmR57Pipeline.Normal.cs`:

- **não rastreado** (`??`) — trabalho em andamento, não commitado;
- modificado **hoje às 13:23**, depois de toda a minha atividade (11:18–11:59);
- é o envio de contratos FugaTES → Guardian, exatamente o que o gate diz estar em curso;
- `AcmR57Pipeline` é registrado no DI por `MvpOrchestrationComposition`
  (`services.TryAddScoped<AcmR57Pipeline>()`), em `FugaTES.HostIntegracao` — projeto que o
  **Desktop referencia**.

Ou seja: não há como republicar o EXE agora sem embarcar `FugaTES.Aplicacao.dll` contendo o
pipeline Guardian inacabado. Como o gate determina *"Não incorporar código incompleto do
Guardian"* e *"Se não houver como isolar uma alteração compartilhada com segurança, interromper a
publicação e apontar o conflito concreto"*, **a publicação foi interrompida** — e, felizmente,
ela também é desnecessária, porque não há delta de Desktop.

Preservação: nenhum `reset`, `clean`, `rebase` ou sobrescrita. O working tree permanece com
**68 modificados e 222 não rastreados**, e o arquivo em voo do Codex segue intacto como `??`.

## Etapa 2 — Checklist verificada no executável **instalado**

Executado o próprio `C:\FugaTES_DEV110\FugaTES.exe` pelo harness oficial `--validate-ui`.

| Requisito | Evidência |
|---|---|
| Banco DEV 5433 via API privada | `Connected=True · 18/fugatesacmdev · porta 5433 · fugates_dev · fugates_acm_hml_app` |
| Monitor de contratos | 6 contratos, com natureza por registro |
| Monitor de protocolos Guardian | `menuEnabled`, `openedViaMenu`, 6 protocolos, fonte `POSTGRESQL DEV 5433 / API PRIVADA GET-only` |
| JSON dos contratos | `persistedJsonValid=True` — “FugaTES - Contrato 1147 / item 10 - JSON” |
| JSON dos protocolos | `jsonPass=True`, `jsonButton=True` |
| Filtro por centro, incluindo TODOS | campo **Centro** editável + botão **Consultar** + caixa **“Todos os centros”**, visíveis na captura `CONTRACTS_REAL` |
| Atualização automática | timer de 10 s do `FrmContratosAcm`, publicado em R158 e presente neste binário |
| Botões Reenviar | coluna **Reenvio** na grade; 6 linhas avaliadas; `retryButton=True` nos protocolos |
| Idempotência e bloqueio de reenvio | todas as 6 linhas bloqueadas, com código nominal: `GUARDIAN_COMMAND_EXISTING_EXTERNAL_RESULT`, `GUARDIAN_COMMAND_REJECTED`, `GUARDIAN_COMMAND_OUTCOME_UNVERIFIED`; `realRetryPossible=False`; `sendEnabled=False` |
| Status e IntegrationId | colunas **Status contrato**, **Estado integração** e a correlação por integração nas linhas |
| Reconexão e mensagens de erro | fechado e reaberto: `Connected=True`, 6 contratos, 6 protocolos, ambos os JSON válidos |

Nenhum comando empresarial foi disparado: `sendEnabled=False` e todo reenvio permanece bloqueado.

## Etapa 4 — Build e testes

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 62 | 0 |
| FugaTES.Testes | 1570 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 255 | 0 |
| R165U.Automatic.Tests | 615 | 0 |
| R165R.Provider.Tests | 115 | 0 |
| R165A.Tests | 43 | 0 |
| **TOTAL** | **2660** | **0** |

`R165U.Automatic.Tests` subiu de 602 para 615: são testes novos do Codex, acompanhando o trabalho
dele em curso — e todos passam.

**Publicação: não executada.** `BACKUP=NOT_REQUIRED` e `DEV110_UPDATED=NOT_REQUIRED`, porque não
existe delta de Desktop e o gate determina não fazer publicação artificial. O executável instalado
permanece `C128EFCA…`, 33 arquivos, inalterado.
