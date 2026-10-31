# R158 — Prontidão do recebimento SAP no DEV unificado

Gate `FUGATES-UNIFIED-SAP-INBOUND-READINESS` (Hera V4). Executor: Claude Code.
Baseline verificada no checkout **antes** de alterar: `Version="1.2.4"`,
`Release="DEV110-SHARED-CONTRACT-PROTOCOL-RETRY-20261028-R1"`, branch
`codex/unified-dev110-20261027`, HEAD `b30de48`.

`REAL_SAP_POSTS=0` · `REAL_GUARDIAN_POSTS=0` · `RECEIVER_ACTIVE=NO` · escritas em banco: 0 ·
migrações: 0 · alterações na VM: 0 · alterações em HML/produção: 0.

---

## 1. Preservação do trabalho do Codex

Inventário do working tree tomado antes de qualquer escrita: **288 entradas** (67 modificadas,
221 não rastreadas). Alterei **8 arquivos existentes**, todos com backup prévio verificado por
hash, e acrescentei arquivos novos. Nenhum arquivo do Codex foi removido, revertido ou
sobrescrito com conteúdo anterior. As funcionalidades de reenvio (Contratos, REST, serviço de
comandos 18763) não foram tocadas.

Backup dos fontes alterados e do `FugaTES.slnx`: scratchpad da sessão, 8 arquivos + solução.

## 2. Correção do recebimento SAP — composição DEV isolada

Pacote novo e aditivo em `deploy/sap-inbound-dev/`:

| Arquivo | Papel |
|---|---|
| `appsettings.receiver.dev.json` | Configuração do receptor, fail-closed |
| `README_ATIVACAO.md` | Procedimento de ativação autorizada |
| `Validar-ReceptorDev.ps1` | Validação **offline**: não instala, não conecta |
| `Rollback-ReceptorDev.ps1` | Rollback / no-op verificável |

Os dez requisitos:

1. **Receptor reutilizado** — nenhuma linha do `SapContractInboundEndpoint` foi alterada; o pacote
   é só configuração. Sem receptor ou persistência paralela.
2. **Datasource exclusivo 5433** — e isto é imposto pelo **código**, não pela configuração:
   `NpgsqlDataSourceFactory.Criar` roteia a role `fugates_acm_hml_app` para `CriarAcm`, que recusa
   qualquer conexão que não seja porta 5433 / `fugates_dev` / schema `fugates` / host loopback /
   `Passfile` absoluto, com `PGPASSWORD` ausente, e instala verificação de identidade física em
   cada conexão. Apontar para 5432, 5434 ou produção é impossível por construção.
3. **Autenticação e origem preservadas** — loopback obrigatório, Basic auth e
   `X-Forwarded-Proto: https` sob `LoopbackBasicHttps`, tudo intocado.
4. **Escopo ACM explícito** — declarado em `MotorIntegracao:AcmInboundWindow`, com Centro, janela,
   parceiros e materiais; nenhum valor inventado, todos os ausentes marcados `<<PENDENTE...>>`.
5. **R57 respeitado** — janela autorizada e time-boxed (≤ 1 dia), com `WindowId`,
   `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc`.
6. **Não é só ligar `ProductiveEnabled`** — a chave está `true` no pacote e mesmo assim a
   composição **recusa**, porque não há escopo ACM. Provado por teste.
7. **Guards intactos** — `SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED` e
   `SAP_INBOUND_SCOPE_NOT_AUTHORIZED` continuam no código e são exercitados pela suíte.
8. **API GET-only 18762 separada** — instância distinta, porta própria pendente de aprovação.
9. **Serviço de comandos 18763 preservado** — não tocado; o rollback verifica as duas portas.
10. **Nenhum worker Guardian / SOAP / REST** — `FUGATES_INTEGRATION_WORKER_ENABLED=false`
    (o hosted service está atrás desse flag), `AcmR57.Normal.ExecutionAuthorized=false`,
    `Callback.Enabled=false`, `AcmProtocolCommand` desabilitado.

Execução da validação no estado atual:

```
TARGET_DATABASE=fugates_dev:5433
WINDOW_ENABLED=False      WORKERS_ENABLED=False
RECEIVER_PACKAGE=BLOCKED
  - pendencias nao preenchidas: <<PENDENTE_PORTA...>>, <<PENDENTE_WINDOWID...>>, ...
  - escopo ACM ausente: ... SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED
```

BLOCKED é o resultado **correto**: o pacote nasce fechado e não pode subir por acidente.

## 3. Persistência no banco unificado — verificada, sem migration

`DATABASE_SCHEMA_READY=PASS`, **sem alterar schema**. A leitura autorizada de 27/10 já devolveu
6 contratos de `fugates_dev:5433` com `jsonValid`, `integrationId` e versão, o que comprova que
`fugates.contrato_sap_toledo_versao`, `contrato_sap_toledo_identidade` e `integracao` existem e
estão consistentes. Nenhuma migration foi preparada ou aplicada porque **nenhuma é necessária**
para o recebimento.

Atomicidade (provada por teste estrutural sobre `ContratoSapToledoVersaoStorePostgreSql`):

```
BeginTransactionAsync(ReadCommitted)
  → insert contrato_sap_toledo_identidade  (upsert + FOR UPDATE)
  → RegistrarCandidatoNaTransacaoAsync     ← H4, lança se != Criada
  → insert contrato_sap_toledo_versao      ← payload jsonb + integracao_id
  → update identidade.ultima_versao
  → COMMIT                                  ← único caminho de aceite
catch { ROLLBACK; throw }
```

Idempotência por `fingerprint_sha256`: reentrega devolve a mesma versão sem gravar; payload
alterado gera nova versão. Duplicação de contrato e item não ocorre.

## 4. Contrato de dados SAP

Os 24 campos recebidos pelo SAP/CPI são preservados pelo modelo e pelo canonicalizador
existentes. Teste `O_contrato_de_dados_sap_preserva_todos_os_campos_recebidos` verifica os 24
nomes no payload canônico, mais: zeros à esquerda (`"0000987001"`, `"0000500123"`), decimais
(`12.345`) e datas OData (`/Date(1761523200000)/`) literais. E verifica que **`GRUIDPROG` e
`MATERIALSAP` não existem** no payload — nada inventado.

## 5. Leitura do Windows Forms

### Dependência do 5432 — removida

Diagnóstico refinado: no arranque padrão o `Program.cs` chama `UnifiedDesktopStartup.Run` e
**`DesktopRuntime.Initialize()` nunca é executado**, logo `DesktopRuntime.Current` é nulo e
nenhuma conexão 5432 chega a ser aberta no perfil unificado. As telas unificadas já usam o 5433
via `PhysicalMonitorClient` (que implementa `IContratosAcmReadRepository` sobre
`/dev/read/contracts`).

Transformei essa ausência em **recusa explícita**: `Runtime/UnifiedProfileGuard.cs` (novo) e as
três composições legadas (`ContratosAcmComposition`, `ProtocolosPreCadastroComposition`,
`MonitorComposition`) passaram a usar `UnifiedProfileGuard.RuntimeLegadoPermitido()`, que devolve
nulo quando `FugaTES.unified.json` declara readback físico. **A configuração do sistema legado não
foi alterada**: `FugaTES.runtime.json` continua intacto e o caminho `--monitor-only` segue
funcionando quando o perfil unificado não está presente.

### Visibilidade de todos os centros

`ContratosAcmConsulta.TodosOsCentros` ("TODOS") + `where @todos or payload_contrato->>'Centro' =
@centro`. O centro continua parametrizado — nenhuma interpolação entrou na consulta — e a
transação segue `SET TRANSACTION READ ONLY` com o mesmo limite de 500. Na tela, uma opção
**"Todos os centros"** ao lado do campo Centro, que permanece editável para escolher outro centro.

### Atualização automática

`FrmContratosAcm` ganhou timer de **10 s** (mesmo intervalo do painel) além do botão Consultar. O
timer apenas reexecuta a leitura: teste garante que a tela não contém `GravarAsync`, `SendAsync(`
nem `HttpClient`, isto é, atualizar nunca dispara reenvio, POST SAP ou chamada Guardian.

### Mensagem de porta — correção do meu próprio diagnóstico anterior

No gate anterior apontei "porta antiga 18761" como defeito. **Isso estava errado** e o registro
precisa ficar: `LocalReadTunnel` sempre faz `-L 127.0.0.1:18761:127.0.0.1:<remota>` e
`PhysicalMonitorClient` exige exatamente a porta local 18761. O 18762 é a porta **remota**. Em vez
de trocar por um valor incorreto, tornei a mensagem inequívoca:

> `API privada: túnel local 127.0.0.1:18761 → porta remota 18762`

A porta remota passou a vir do perfil (`PhysicalReadConnection.RemoteReadPort`), não de um
literal. Visível na captura `MAIN-render.png` do app publicado.

## 6. Testes

Nova suíte `tests/R158.SapInboundReadiness.Tests` — **26 testes, 26 aprovados**, cobrindo os 14
cenários exigidos: contrato válido; múltiplos itens; centro ≠ 3011; duplicado com mesmo
fingerprint; payload alterado; falha de H4 com rollback; falha de persistência com rollback;
escopo ausente e janela expirada; credenciais inválidas e `SecurityMode` inválido; payload
inválido; projeção consultável após COMMIT; atualização da grade; JSON da projeção persistida;
workers e Guardian inativos. Mais o pacote do receptor nascendo fechado e a ausência do 5432.

### Regressão — 12 falhas preexistentes, 0 regressões introduzidas

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 26 | 0 |
| FugaTES.Testes | 1569 | 1 |
| R164E.Integration.Tests | 35 | 0 |
| R165A.Tests | 43 | 0 |
| R165U.Automatic.Tests | 594 | 8 |
| FugaTES.WinForms.Monitor.R165M.Tests | 254 | 1 |
| R165G.TicketIdentity.Tests | 24 | 0 |
| R165R.Provider.Tests | 113 | 2 |
| FugaTES.Masterdata.R28.Tests | 17 | 0 |
| FugaTES.CarrierBinding.R37.Tests | 32 | 0 |
| FugaTES.SingleCreate.R31 / R41 | 128 | 0 |
| **TOTAL** | **2835** | **12** |

Os nomes das falhas são **exatamente os mesmos** medidos antes de qualquer alteração minha:
`PackagedProfileTests` (2), `R165ZRestMotorParityTests` (3), `RestPostmanParityTests`,
`Pilot1140PreparationTests`, `R165ACValidationDiagnosticsTests`,
`AcmMonitorStartupTests.Compiled_entrypoint...`, e `CarrierSyncPollingClockTests` (instável — passou
na reexecução). Todas tratam de **perfis empacotados, paridade do binário instalado e entrypoint
compilado**: são o estado de publicação em andamento do Codex, não a cadeia de recebimento.
Nenhuma expectativa de teste foi alterada ou excluída para obter PASS.

## 7. Publicação do DEV110

`BUILD=PASS` (Release, 0 erros, 3 avisos). `BACKUP=PASS`: `C:\FugaTES_DEV110_BACKUP_R158_20261028_125915Z`,
33/33 arquivos, **0 divergências de hash**.

Publicados **27 binários**; preservadas **6/6** configurações instaladas byte-idênticas
(`FugaTES.unified.json`, `FugaTES.runtime.json`, `FugaTES.carrier.json`,
`FugaTES.retry-command.json`, `AcmMonitor.R165M.json`, `profiles/R165AL_PILOT_PLAN.json`). Total
continua 33 arquivos — nenhum arquivo novo foi introduzido na instalação.

```
FugaTES.exe              C128EFCAAE6C0E2787013523218E6C8B5DE59B340BF0B8E5F78597B86BCD3D4B
FugaTES.dll              BFDB145CE0215D440E38479A97ECD5F8E43EBBCA7DD2A2A84278FD3644891036
FugaTES.Persistencia.dll F98E7BA5684709149206FB91B60BD932CA53DC03F1B0762487AD7A40F227D252
FugaTES.Aplicacao.dll    87249C8C29041F5CB78C50DF8EAC3EEB008863778858DF02D0BAFD90693EC9D4
```

Drift reverificado imediatamente antes da escrita: versão 1.2.4, HEAD `b30de48`, exe instalado
ainda `8EEB9612…` de 08:46 — nenhuma alteração concorrente foi sobrescrita.

`DEV110_EXECUTION=PASS`: o app abriu em v1.2.4 pelo harness oficial `--validate-ui`, renderizou o
painel e a tela de Integrações (`MAIN-render.png`, `INTEGRATIONS-render.png`) e foi encerrado.

## 8. Pendência física que impede a comprovação de leitura ao vivo

`DEV110_CONTRACT_READBACK=BLOCKED`, por precondição de ambiente, **não por código**:

> `DESCONECTADO — Arquivo privado da chave de leitura ausente ou inválido.`

`C:\Users\TI\AppData\Local\FugaTES\DEV110\READ_API_KEY` **não existe** nesta máquina (o diretório
também não). Em 27/10 a evidência mostra a chave presente e leituras com sucesso; nesta sessão ela
não está provisionada. Não a criei nem ampliei ACL alguma — é credencial do operador, e a chave
correspondente vive na API privada da VM.

Com a chave restaurada, a leitura volta sem qualquer alteração de código: o destino esperado já
aparece corretamente na tela como `18/fugatesacmdev | fugates_dev | porta física 5433`.

## PHYSICAL_ACTIVATION_REQUIREMENTS

1. **Chave privada de leitura** `READ_API_KEY` reprovisionada no perfil do operador (64 hex, ACL
   restrita a TI/SYSTEM), para destravar a grade, o JSON e o monitor ao vivo.
2. **Republicar a API privada de leitura DEV (18762)** — GET-only — para que o seletor `TODOS`
   passe a valer. `FugaTES.Persistencia` roda dentro dela; a instalada é de 27/10 e trata `TODOS`
   como centro literal. A tela já avisa isso em vez de afirmar ausência de registros.
3. **Autorização da Hera** para porta, rota, escopo e janela do receptor; preenchimento dos
   `<<PENDENTE...>>` e `AcmInboundWindow.Enabled=true` com janela ≤ 1 dia e listas de parceiros e
   materiais não vazias.
4. **Privilégios PostgreSQL** do role `fugates_acm_hml_app` no 5433 para `INSERT`/`UPDATE` em
   `contrato_sap_toledo_identidade`, `contrato_sap_toledo_versao` e `integracao`. A evidência
   disponível comprova apenas leitura. Sem `GRANT` neste gate.
5. **Reverso do SAP/CPI** para a rota aprovada, pelo proxy que fornece `X-Forwarded-Proto: https`.
6. **Envio SAP real** dentro da janela aberta — único meio de comprovar a cadeia ponta a ponta.
