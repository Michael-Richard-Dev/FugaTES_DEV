# R159 — Prontidão física do receptor SAP no DEV unificado

Gate `FUGATES-SAP-INBOUND-PHYSICAL-READINESS` (Hera V4). Baseline `DEV110_1.2.4`.
`RECEIVER_ACTIVATION=NOT_EXECUTED`.

Nada foi alterado no ambiente: `NO_VM_CHANGES`, `NO_CREDENTIAL_COPY`, `NO_DATABASE_WRITES`,
`NO_GRANTS`, `NO_SAP_POST`, `NO_GUARDIAN_POST`, `NO_WORKER_ACTIVATION`,
`NO_HML_SERVICE_CHANGES`, `NO_MAIN_PUSH`. Nenhuma credencial foi exibida, copiada ou recriada.

---

## 1. READ_API_KEY — causa-raiz

**`READ_KEY_ROOT_CAUSE=MSIX_PACKAGED_APP_LOCALAPPDATA_REDIRECTION`**

A chave existe e é válida. Ela só não está no caminho que o Desktop resolve.

| Verificação | Resultado |
|---|---|
| Identidade da sessão | `NT-TI-256\TI`, SID `S-1-5-21-1842992099-521793823-1132869621-1001` — **é o operador autorizado** (mesmo SID de `AllowedSids` e de `OperatorSid`) |
| Perfil | `LOCALAPPDATA=C:\Users\TI\AppData\Local` — correto, sem redirecionamento de perfil |
| Caminho do perfil (`FugaTES.unified.json → ReadKeyFile`) | `C:\Users\TI\AppData\Local\FugaTES\DEV110\READ_API_KEY` — **ausente** |
| Natureza da ausência | `[IO.Directory]::GetFileSystemEntries` lança **`DirectoryNotFoundException`** — não é acesso negado nem invisibilidade de sandbox |
| Prova de que nunca existiu ali | `C:\Users\TI\AppData\Local\FugaTES` tem `LastWriteTime` de **07/10/2026 17:44:26**. Criar ou apagar um filho atualizaria essa data; ela é anterior a todo o trabalho unificado de 27/10. O diretório lista apenas `Secrets`. |
| Onde a chave realmente está | `C:\Users\TI\AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\FugaTES\DEV110\READ_API_KEY` |
| Metadados dessa chave | 64 bytes, criada 27/10/2026 17:21:53, herança protegida, proprietário `NT-TI-256\TI`, Allow = `AUTORIDADE NT\SISTEMA` + `NT-TI-256\TI` — **exatamente o contrato exigido** por `UnifiedDesktopSession` |
| `COMMAND_BEARER` (serviço 18763) | mesma redireção, mesmo diretório virtualizado |

**Explicação.** O Codex roda como aplicativo empacotado (MSIX). Quando ele gravou em
`%LOCALAPPDATA%\FugaTES\DEV110\`, o Windows redirecionou a escrita para o contêiner
`...\Packages\OpenAI.Codex_...\LocalCache\Local\`. O `FugaTES.exe` do DEV110 roda **fora** do
pacote e resolve o caminho real do perfil, onde o diretório nunca foi criado. Isso também explica
`READ_KEY_METADATA_VERIFICATION.json` de 27/10 declarar `FileExists: true` com
`ClaudeSessionIdentity: NOT_ATTESTED`: a verificação correu dentro do contêiner.

Não é defeito de código, não é ACL incorreta e não é problema de identidade. A chave está certa,
no lugar errado para o consumidor.

**Correção mínima (do operador, não executada aqui — `NO_CREDENTIAL_COPY`):** provisionar a chave
em `C:\Users\TI\AppData\Local\FugaTES\DEV110\READ_API_KEY`, 64 caracteres hex, herança desativada
e ACL Allow apenas para `S-1-5-18` e o SID do operador; idem
`DEV110\command\COMMAND_BEARER`. Alternativa equivalente: apontar `ReadKeyFile` e
`CommandCredentialFile` em `FugaTES.unified.json` / `FugaTES.retry-command.json` para um diretório
fora de qualquer contêiner MSIX, com a mesma ACL. **Não copiar o arquivo existente**: gerar nova
chave e registrá-la também na API privada da VM, em gate próprio.

`DEV110_READBACK=BLOCKED` — exclusivamente por esta precondição. O código já aponta para o destino
correto, visível na própria tela: `18/fugatesacmdev | fugates_dev | porta física 5433 |
API privada: túnel local 127.0.0.1:18761 → porta remota 18762`.

---

## 2. Delta da API privada GET-only 18762 — pronto, não instalado

**`READ_API_18762_RELEASE=READY`**

Pacote: `FUGATES_UNIFIED_DEV_READ_API_TODOS.zip`
SHA-256 `2C072DC3EA84BBFC28C74024275185DCAC272CBA67AA3E5B387CCBFD01BFF262`, 1 271 833 bytes,
18 arquivos (os mesmos 17 do contrato instalado + `MANIFEST.json`), modo `--dev-unified-read-only`,
porta 18762, `credentialsIncluded=false`, `installed=false`.

Delta medido contra o pacote instalado em 27/10 (`ECF8546046…`):

| Situação | Arquivos |
|---|---|
| **Alterados** | `FugaTES.Aplicacao.dll`, `FugaTES.Persistencia.dll`, `FugaTES.Callback.Http.dll`, `FugaTES.Dominio.dll`, `FugaTES.HostIntegracao.dll`, `FugaTES.Infraestrutura.dll`, `FugaTES.Integracao.Guardian.dll`, `FugaTES.Integracao.SAP.dll`, `FugaTES.Integracao.Toledo.dll` |
| **Idênticos** | `Npgsql.dll`, `Microsoft.Extensions.Hosting.*`, `System.ServiceProcess.ServiceController.dll`, os três `*.deps.json`/`*.runtimeconfig.json` |

**Ressalva honesta:** apenas dois desses binários mudam por causa do seletor `TODOS`
(`FugaTES.Aplicacao.dll` → `ContratosAcmConsulta.TodosOsCentros`; `FugaTES.Persistencia.dll` →
`where @todos or payload_contrato->>'Centro' = @centro`). Os outros sete diferem porque a árvore
avançou desde 27/10 (trabalho do Codex) e porque a compilação não é determinística. Republicar
leva **todo** o avanço do fonte, não só o `TODOS`. A decisão é da Hera.

**Preservação garantida:** o pacote não altera a porta (18762), não toca o túnel local 18761 nem o
serviço de comandos 18763, não embute credenciais, não habilita worker e mantém
`default_transaction_read_only=on`. A instalação cria um diretório novo com carimbo de data sob
`/home/michael/.fugates-unified-dev-read/`, preservando o leitor anterior — exatamente como em
27/10. Rollback = reapontar para o diretório anterior e encerrar o PID novo.

Defeito de build encontrado e **não corrigido** (fora de escopo, arquivos do Codex): publicar
`FugaTES.Callback.Http` falha com `NETSDK1152` por `appsettings.json` duplicado entre
`HostIntegracao` e `Callback.Http`. Contornei na linha de comando com
`-p:ErrorOnDuplicatePublishOutputFiles=false`, sem alterar o projeto. Convém corrigir o csproj em
gate próprio.

---

## 3. Privilégios do receptor no DEV 5433

**`INBOUND_DATABASE_ACL=BLOCKED`** — por evidência documental; a confirmação ao vivo depende de uma
sessão PostgreSQL que esta etapa não possui.

O receptor escreve em exatamente três tabelas, numa única transação:

| Tabela | Ações |
|---|---|
| `fugates.contrato_sap_toledo_identidade` | `INSERT`, `UPDATE`, `SELECT … FOR UPDATE` |
| `fugates.contrato_sap_toledo_versao` | `INSERT`, `SELECT` |
| `fugates.integracao` | `INSERT` (H4) |

A única concessão registrada para `fugates_acm_hml_app` é a de 27/10
(`003_MONITOR_COLUMN_READ_ACL_DEV.sql`, `EXECUTION_RESULT.json` → `columnSelectAdded: 12`):
**12 `SELECT` por coluna**, em `integracao`, `tentativa_integracao` e `auditoria_integracao`. O
próprio cabeçalho declara *"No writer grants, no data mutation, no default privileges or role
membership changes"*, e a guarda exige que a role **não** seja membro de `fugates_dev_owner`.

Ou seja: a role está provisionada como **leitora**. Não há evidência de `INSERT`/`UPDATE` nas três
tabelas do receptor — e, sem eles, o primeiro POST real falharia na transação e faria `ROLLBACK`
integral, sem aceitar o contrato.

Entreguei a verificação somente leitura, sem `GRANT`:
`deploy/sap-inbound-dev/VERIFICAR_PRIVILEGIOS_RECEPTOR.sql` — confere identidade física do
destino, não-privilegiamento da role, `has_table_privilege` das 7 combinações, `USAGE` de
sequências, e imprime o veredito `INBOUND_DATABASE_ACL=PASS|BLOCKED`. Roda em transação
`READ ONLY`. **Os GRANT de escrita, se autorizados, são objeto de gate próprio.**

---

## 4. Plano físico de ativação — pronto, não executado

**`PHYSICAL_CHANGE_PLAN=READY`** · **`R57_ADMISSION_CONFIGURATION=READY`** (estrutura pronta,
janela **não criada**) · **`SAP_CPI_ROUTE=PENDING`**

| # | Item | Estado |
|---|---|---|
| 1 | **Porta e listener** | Instância isolada, loopback. Valor `<<PENDENTE_PORTA_LOOPBACK_APROVADA_HERA>>`. Não pode ser 18761/18762/18763 — o validador recusa. |
| 2 | **Rota corporativa** | `POST /api/v1/integracoes/sap/contratos`. O caminho de aplicação é fixo no código; o que falta é a **rota de ingresso corporativa** até o loopback. `PENDING`. |
| 3 | **Identidade da origem SAP/CPI** | Não atestada. Nenhum identificador de origem foi fornecido; não inventei nenhum. `PENDING`. |
| 4 | **Autenticação e cabeçalhos** | Loopback obrigatório + Basic (`SapInbound:BasicAuth:*`) + `X-Forwarded-Proto: https` sob `SecurityMode=LoopbackBasicHttps`. Já exigidos pelo código; credenciais `<<PENDENTE>>`. |
| 5 | **Escopo ACM** | `MotorIntegracao:AcmInboundWindow` com `Centro=3011` e exclusão dos históricos. `AllowedSapPartners` e `AllowedSapMaterials` **vazios** → precisam da Hera. |
| 6 | **Janela R57 com expiração** | `WindowId` (GUID `N`), `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` em UTC, duração ≤ 1 dia. **`Enabled=false`; janela não criada.** |
| 7 | **Destino único `fugates_dev:5433`** | Imposto pelo código: `NpgsqlDataSourceFactory.CriarAcm` recusa qualquer outro host/porta/base/schema e verifica identidade física a cada conexão. **Já garantido.** |
| 8 | **Workers Guardian desabilitados** | `FUGATES_INTEGRATION_WORKER_ENABLED=false` (o hosted service está atrás desse flag), `AcmR57.Normal.ExecutionAuthorized=false`, `Callback.Enabled=false`, `AcmProtocolCommand` desabilitado. **Já garantido.** |
| 9 | **Rollback** | `Rollback-ReceptorDev.ps1` — restaura do backup ou remove a encenação, e verifica que 18761/18762/18763 seguem intactos. No estado atual devolve `ROLLBACK=NOT_REQUIRED`. |

Estado atual da validação (fail-closed, como deve ser):

```
TARGET_DATABASE=fugates_dev:5433   WINDOW_ENABLED=False   WORKERS_ENABLED=False
RECEIVER_PACKAGE=BLOCKED
  - pendencias nao preenchidas: <<PENDENTE_PORTA...>>, <<PENDENTE_WINDOWID...>>, ...
  - escopo ACM ausente -> SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED
```

---

## 5. Reconciliação das 12 falhas — nenhum defeito funcional

**`REGRESSION_12_FAILURES=CLASSIFIED`**

| Classe | Qtd. | Testes | Causa comprovada |
|---|---|---|---|
| **Ambiente / raiz de evidência** | 8 | `RestPostmanParityTests`, `R165ZRestMotorParityTests` (3), `Pilot1140PreparationTests`, `R165ACValidationDiagnosticsTests` e demais de `R165U.Automatic` | Resolvem a evidência por `FUGATES_TEST_EVIDENCE_ROOT`, **não definida em nenhum escopo** (processo, usuário, máquina) → `Frozen approved evidence not found.` / `Assert.NotNull … Value is null` |
| **Ambiente / pacote isolado** | 2 | `PackagedProfileTests` (2) | `System.InvalidOperationException: Set R165R_PACKAGE_ROOT to the isolated, disabled release candidate.` — variável **não definida em nenhum escopo** |
| **Artefato publicado / expectativa defasada** | 1 | `AcmMonitorStartupTests.Compiled_entrypoint_has_unified_safe_startup_and_explicit_read_monitor_mode` | `Expected: 1.2.3.0 / Actual: 1.2.4.0`. O teste fixa a versão do assembly em 1.2.3 enquanto o Codex promoveu para **1.2.4** — que é a baseline deste próprio gate. A expectativa é que está defasada. |
| **Instabilidade de tempo** | 1 | `CarrierSyncPollingClockTests.Long_cycle_skips_ticks_without_overlap_or_backlog` | Sensível a temporização; **passou na reexecução** da mesma árvore |
| **Defeitos funcionais reais** | **0** | — | — |

Nenhuma expectativa foi alterada, relaxada ou excluída para obter PASS. As 10 de ambiente passam a
valer assim que as duas variáveis apontarem para as raízes de evidência corretas; a de versão
exige decisão sobre promover a expectativa para 1.2.4 (gate do Codex, não meu).

---

## 6. Preservação

Código consolidado, alterações do Codex, `FugaTES.exe` v1.2.4 e serviços instalados **intactos**.
Nesta etapa criei apenas três arquivos novos — `VERIFICAR_PRIVILEGIOS_RECEPTOR.sql`, este
documento e o pacote de evidência fora do repositório — e não modifiquei nenhum arquivo existente.
Nenhum processo foi iniciado ou parado na VM; nenhuma porta foi aberta.
