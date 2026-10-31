# R159B — Restauração do readback DEV110 e preparo da API de leitura

Gate `FUGATES-R159-DEV110-READBACK-RESTORATION`. Baseline `DEV110_1.2.4`.
`SAP_RECEIVER_ACTIVE=NO` · `R57_WINDOW_ACTIVE=NO` · `DATABASE_WRITES=0` · `EXTERNAL_POSTS=0` ·
nenhuma alteração na VM.

---

## Missão 1 — Credenciais locais restauradas

`READ_API_KEY_LOCAL=PASS` · `COMMAND_BEARER_LOCAL=PASS`

Origem comprovada antes de qualquer escrita:

| Arquivo | Origem (contêiner MSIX do Codex) | Validade |
|---|---|---|
| `READ_API_KEY` | 64 bytes, ACL protegida, owner `NT-TI-256\TI` | forma conferida: 64 caracteres, todos hexadecimais |
| `command\COMMAND_BEARER` | 65 bytes, ACL protegida, owner `NT-TI-256\TI` | perfil instalado declara `UntilUtc=2026-11-27T11:34:36Z` (vigente), `OperatorSid` confere, `Homologated=true` |

Provisionado em `C:\Users\TI\AppData\Local\FugaTES\DEV110\`:

```
READ_API_KEY     copia_identica=True  bytes=64  acl_ok=True  reparse=False
COMMAND_BEARER   copia_identica=True  bytes=65  acl_ok=True  reparse=False
identidades Allow: S-1-5-18, S-1-5-21-1842992099-521793823-1132869621-1001
```

Herança desativada, proprietário TI, Allow restrito a **SYSTEM + TI** — exatamente o contrato que
`UnifiedDesktopSession` exige (`AreAccessRulesProtected` e nenhuma identidade fora de
`{ownSid, S-1-5-18}`). Igualdade verificada por comparação de hash, **sem exibir, registrar ou
exportar o conteúdo**; nenhum segredo entrou no projeto nem nas evidências. Nenhuma chave nova foi
gerada e nada foi rotacionado no servidor.

### Comprovação prática pelo FugaTES.exe real

`DEV110_PHYSICAL_READBACK=PASS`

```
Connected      : True
Cluster        : 18/fugatesacmdev     ServerPort : 5433
Database       : fugates_dev          Role       : fugates_acm_hml_app
Message        : "Leitura unificada DEV validada pela API privada."
contratos lidos: 6
JSON           : "Contrato 1147 / item 10 — JSON", persistedJsonValid=True, payloadExported=False
sendEnabled    : False
```

Os seis contratos vieram com `IntegracaoId`, versão e natureza (`SIMULACAO_CONFIRMADA` para o
piloto; `PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA` para os demais). O **Reenviar** foi avaliado
linha a linha e **todas as seis** vieram `canRequest=False`, com motivo nominal
(`SIMULATION`, `ORIGIN_UNPROVEN`, `RECONCILIATION_REQUIRED`) — os bloqueios existentes estão
preservados e nenhuma operação empresarial foi executada.

### Fechamento e reabertura

`DEV110_REOPEN=PASS`

Fechado e reaberto, reconectou em `18/fugatesacmdev | 5433 | fugates_dev | fugates_acm_hml_app`,
6 contratos, JSON válido, `sendEnabled=False`, 21 arquivos de evidência.

**Registro honesto:** a primeira tentativa de reabertura falhou
(`UNIFIED_UI_SMOKE_FAILED`). Causa identificada: o processo `ssh.exe` do túnel da execução
anterior continuava escutando `127.0.0.1:18761` depois que o app foi encerrado, e a validação de
binding do novo processo não aceita um listener remanescente. Encerrando também o auxiliar do
túnel, a reabertura passou. É comportamento do ciclo de vida do túnel, não defeito do readback:
quem reabre o app deve garantir que o túnel anterior foi liberado.

---

## Missão 2 — API de leitura 18762

`READ_API_18762_PACKAGE=PASS` · `BINARY_PARITY=PASS` · `NETSDK1152=RESOLVED`

### Os 9 binários divergentes — explicados por completo

Primeiro, **desmenti minha própria hipótese anterior**: eu havia sugerido não-determinismo de
compilação. Duas publicações consecutivas da mesma fonte produziram **9 de 9 assemblies
byte-idênticos**. O build é determinístico; a divergência tem outra causa.

A causa real está no `ProductVersion`, que embute o commit (SourceLink):

```
FugaTES.Dominio.dll  27/10 instalado : 1.0.0+f582c0ecf02ce3a2eaa04e7cb8fca09997c4e2e0
FugaTES.Dominio.dll  atual           : 1.0.0+b30de487e7f9ea13cf829ef353cba932db455f1c
```

O pacote instalado foi construído entre **19:22 e 19:39 de 27/10**; o commit `b30de48` é de
**20:32 de 27/10** — depois. Logo **todo** assembly recompilado passou a carregar outro carimbo de
revisão, mesmo sem uma linha de código alterada. Isso explica `FugaTES.Dominio.dll` e
`FugaTES.Integracao.Toledo.dll`, que têm **zero** arquivos modificados ou novos.

| Classe | Assemblies | Justificativa |
|---|---|---|
| **Mudança funcional (minha, R158)** | `FugaTES.Aplicacao.dll`, `FugaTES.Persistencia.dll` | seletor `TODOS`: `ContratosAcmConsulta.TodosOsCentros` e `where @todos or payload_contrato->>'Centro' = @centro` |
| **Evolução do Codex + carimbo de commit** | `FugaTES.Callback.Http.dll` (15 modif./9 novos), `FugaTES.HostIntegracao.dll` (4/18), `FugaTES.Integracao.Guardian.dll` (6/10), `FugaTES.Integracao.SAP.dll` (4/13), `FugaTES.Infraestrutura.dll` (0/1) | trabalho não commitado do Codex nesses projetos |
| **Somente carimbo de commit** | `FugaTES.Dominio.dll`, `FugaTES.Integracao.Toledo.dll` | nenhum arquivo modificado ou novo; apenas `+f582c0e` → `+b30de48` |

Nenhum dos 7 carrega mudança funcional minha.

### NETSDK1152 — corrigido na fonte canônica

Causa: `FugaTES.Callback.Http` e `FugaTES.HostIntegracao` publicam, cada um, um
`appsettings.json`, e o primeiro referencia o segundo — dois arquivos disputando o mesmo caminho
relativo na publicação.

Correção focal, em `FugaTES.Callback.Http.csproj`, **sem desligar a verificação do SDK** (nada de
`ErrorOnDuplicatePublishOutputFiles=false`) e **sem alterar o HostIntegracao**: um alvo após
`ComputeResolvedFilesToPublishList` remove da lista de publicação apenas as cópias de
`appsettings*` vindas de fora do diretório deste projeto.

Verificado:
- `dotnet publish` do Callback.Http conclui **sem contorno de linha de comando**;
- o `appsettings.json` publicado é o **do próprio Callback.Http**;
- o HostIntegracao, publicado sozinho, continua levando `appsettings.json` e
  `appsettings.Development.json` próprios;
- `dotnet build FugaTES.slnx -c Release` → **0 erros, 0 avisos** (antes eram 3 avisos).

### Pacote reproduzível

`FUGATES_UNIFIED_DEV_READ_API_TODOS_R2.zip`
SHA-256 `836A9D58826E1460E3CB8589B92D98A2484C2BBFAA4CE9FCF7605A0D3F27D703`, 1 271 885 bytes,
18 arquivos (os 17 do contrato instalado + `MANIFEST.json`), construído pela fonte canônica já
corrigida, `builtWithoutWorkaround=true`, `sourceCommit=b30de48…`, `installed=false`.

Validação do pacote:
- `runtimeconfig.json` **idêntico** ao instalado;
- **zero** `appsettings*.json` no pacote (modo GET-only não os usa);
- **zero** credenciais;
- modo `--dev-unified-read-only`, porta 18762, banco 5433 — nesse modo o `Program.cs` retorna
  antes de construir o host de entrada, então nenhuma rota de recepção é mapeada e nenhum worker
  é registrado.

**Rollback preservado:** o pacote instalado de 27/10
(`ECF8546046105C9EE875448D56E4741E986EF418BDC47F798285D140B98CF782`) continua íntegro em
`/home/michael/.fugates-unified-dev-read/20261027_ECF854604610` e nas evidências locais. A
instalação cria diretório novo com carimbo de data; o rollback é reapontar para o anterior e
encerrar o PID novo. **A API instalada não foi substituída.**

---

## Missão 3 — Identidade de escrita

`SAP_WRITER_IDENTITY_PLAN=READY`. Nenhum `CREATE ROLE`, `GRANT` ou DDL executado.

Proposta completa em [`PROPOSTA_IDENTIDADE_ESCRITA_SAP.md`](../../deploy/sap-inbound-dev/PROPOSTA_IDENTIDADE_ESCRITA_SAP.md).

Recomendação central: **não conceder `INSERT`/`UPDATE` à role leitora.** `fugates_acm_hml_app`
serve a API 18762, que abre conexões `read_only`; ampliá-la daria capacidade de escrita, no
catálogo, a um componente que só lê. Proposta: role separada `fugates_sap_inbound_app`, sem
atributos privilegiados, não membro do owner, com exatamente:

- `contrato_sap_toledo_identidade`: `SELECT`, `INSERT`, `UPDATE(ultima_versao)`
- `contrato_sap_toledo_versao`: `SELECT`, `INSERT`
- `integracao`: `INSERT`
- `USAGE` nas sequências dependentes

Sem `DELETE`, sem `TRUNCATE`, sem privilégio em `tentativa_integracao`/`auditoria_integracao`
(são do worker, que segue desabilitado), sem `DEFAULT PRIVILEGES`.

**Ponto de atenção levantado:** hoje as guardas reforçadas de conexão (`CriarAcm`: porta 5433,
base, schema, host loopback, `Passfile`, verificação de identidade física por conexão) são
acionadas pela comparação com `AcmApplicationRole`. Adotando uma role nova, é preciso uma
alteração de **uma linha** em `NpgsqlDataSourceFactory` para submetê-la às mesmas guardas — caso
contrário ela cairia no caminho genérico. Isso pertence ao gate de ativação.

O mecanismo R57 permanece intacto: escopo ACM obrigatório, janela autorizada com expiração ≤ 1 dia
e destino único `fugates_dev:5433`.

---

## Missão 4 — Regressão reexecutada

`TESTS_PASS=2846` · `TESTS_FAIL=1`

Nenhuma expectativa foi alterada, relaxada ou excluída. O que mudou foi **o ambiente**, que passou
a apontar para os artefatos corretos que já existiam em disco:

- `FUGATES_TEST_EVIDENCE_ROOT=C:\Users\TI\Documents\ChatGPT\FugaTES`
  (contém `evidence/R165Y_AUTO_1144/PILOT_1144_RESULT.json`)
- `R165R_PACKAGE_ROOT=…\FugaTES_DEV\test-fixtures\R165R_DISABLED_PROFILE`
  (fixture do próprio repositório, com `runtime/host` e `runtime/callback`)

| Suíte | Antes | Agora |
|---|---|---|
| R158.SapInboundReadiness.Tests | 26 / 0 | **26 / 0** |
| FugaTES.Testes | 1569 / 1 | **1570 / 0** |
| R164E.Integration.Tests | 35 / 0 | **35 / 0** |
| R165A.Tests | 43 / 0 | **43 / 0** |
| R165U.Automatic.Tests | 594 / **8** | **602 / 0** |
| FugaTES.WinForms.Monitor.R165M.Tests | 254 / **1** | **254 / 1** |
| R165G.TicketIdentity.Tests | 24 / 0 | **24 / 0** |
| R165R.Provider.Tests | 113 / **2** | **115 / 0** |
| FugaTES.Masterdata.R28.Tests | 17 / 0 | **17 / 0** |
| FugaTES.CarrierBinding.R37.Tests | 32 / 0 | **32 / 0** |
| FugaTES.SingleCreate.R31 / R41 | 128 / 0 | **128 / 0** |
| **TOTAL** | 2835 / 12 | **2846 / 1** |

### Histórico das 12 falhas anteriores

| Classe | Qtd | Situação |
|---|---|---|
| Ambiente — raiz de evidência (`FUGATES_TEST_EVIDENCE_ROOT`) | 8 | **APROVADOS** com a variável correta |
| Ambiente — pacote isolado (`R165R_PACKAGE_ROOT`) | 2 | **APROVADOS** com a variável correta |
| Instabilidade de tempo (`CarrierSyncPollingClockTests`) | 1 | **APROVADO** nesta execução |
| Expectativa de versão | 1 | **AINDA BLOQUEADO** |

A única falha remanescente é
`AcmMonitorStartupTests.Compiled_entrypoint_has_unified_safe_startup_and_explicit_read_monitor_mode`:
`Expected 1.2.3.0 / Actual 1.2.4.0`. O teste fixa a versão do assembly em **1.2.3** enquanto o
produto — e a baseline declarada deste gate — é **1.2.4**. **Não alterei a expectativa**, porque
isso mascararia a divergência: a decisão de promover a expectativa para 1.2.4 é de quem definiu a
versão. Nenhum teste foi ignorado.

---

## Preservação

`FugaTES.exe` v1.2.4 (`C128EFCA…`) e os 33 arquivos do DEV110 intactos — nenhuma publicação nesta
etapa. API 18762 instalada **não substituída**; túnel 18761 e serviço de comandos 18763
preservados. Nesta etapa modifiquei um único arquivo existente, `FugaTES.Callback.Http.csproj`
(correção do NETSDK1152, com backup), e criei três documentos. Nenhum segredo foi exibido,
registrado, exportado ou incorporado ao projeto.
