# DEV110 — Diagnóstico da continuidade do recebimento automático SAP → FugaTES

Gate: `FUGATES-DEV110-SAP-INBOUND-RESTORATION` (Hera V4). Executor: Claude Code.
Modo: diagnóstico focal. **Nenhum arquivo de código foi alterado neste gate.**
`REAL_SAP_POSTS=0` · `REAL_GUARDIAN_POSTS=0` · escritas em banco: 0 · alterações na VM: 0.

---

## Missão 1 — O que já existe (confirmado por código e configuração)

| # | Pergunta | Resposta comprovada |
|---|---|---|
| 1 | Qual serviço recebe os contratos SAP | O host web `FugaTES.Callback.Http`. Em `Program.cs`, `SapContractInboundEndpoint.MapDurable(app)` é mapeado **somente** quando `InboundHttpHostComposition.Configurar` devolve `SapProductive=true`. |
| 2 | Endpoint e rota | `POST /api/v1/integracoes/sap/contratos` (`SapContractInboundEndpoint.Route`). Exige loopback, Basic auth e, em `SecurityMode=LoopbackBasicHttps`, o header `X-Forwarded-Proto: https`. |
| 3 | Datasource efetivo do serviço | `ConnectionStrings:FugaTES`, exigido por `SapContractInboundProductiveComposition`. A configuração de referência do repositório (`configuration/appsettings.inert.json`) declara `Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates`. |
| 4 | Banco que recebia antes da unificação | O cluster legado na porta **5432**. É para lá que o caminho instalado antigo ainda aponta: `C:\FugaTES_DEV110\FugaTES.runtime.json` → túnel local 15432 → `RemoteDatabasePort: 5432`, `fugates_dev`, `fugates_dev_app`. É o histórico citado pelo gate. |
| 5 | Banco canônico atual | `18/fugatesacmdev` porta **5433**, `fugates_dev`, role `fugates_acm_hml_app`, somente leitura pela API privada. Confirmado pela própria API em `evidence/UNIFIED_POSTGRESQL_DEV_20261027/DEV_API_READ_AFTER_ACL.json`. |
| 6 | Quem grava contrato, versão, IntegrationId, H4 e journal | `AceitarContratoSapToledoDuravelCasoDeUso` → `ContratoSapToledoVersaoStorePostgreSql`. Numa **única transação**: `contrato_sap_toledo_identidade` (upsert + `FOR UPDATE`), H4 via `RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync` (lança se não for `Criada`), `contrato_sap_toledo_versao` com `payload_contrato` jsonb e `integracao_id`, e atualização de `ultima_versao`. Idempotência por `fingerprint_sha256`. |
| 7 | Como os dados chegam à API de leitura DEV | `DevUnifiedReadApi` (modo `--dev-unified-read-only`), exclusivo Linux, listener `127.0.0.1:18762`, conexão `default_transaction_read_only=on`, autenticação por header `X-R165AL-Read-Key` + loopback. Rotas GET `identity`, `contracts`, `monitor`, `detail`, `protocol`, `masterdata`. |
| 8 | Como o Windows Forms atualiza a listagem | `PainelInicialForm`: `connectionTimer` de **10 s** chamando `UnifiedDesktopSession.RefreshConnectionAsync()`, mais o botão explícito **“Conectar / Atualizar leitura”**. `FrmMonitorMvp` e `FrmTransportadoras` têm timers próprios (este último de 30 s). |

### Os dois caminhos de leitura instalados (ambos preservados)

```
UNIFICADO   FugaTES.unified.json  → túnel SSH 192.168.17.40 → 18762 → fugates_dev : 5433  (canônico)
LEGADO      FugaTES.runtime.json  → túnel local 15432       → remoto 5432          (pré-unificação)
```

`UnifiedDesktopSession` (versão **1.2.3**, a baseline do gate) é o monitor ligado ao 5433.
`FrmContratosAcm`, via `ContratosAcmComposition` → `DesktopRuntime.CreateReadSource()`, usa o datasource
do `FugaTES.runtime.json`, isto é, o **5432**.

---

## Missão 2 — Reconciliação com a unificação

| Hipótese do gate | Veredito |
|---|---|
| Datasource apontando para banco antigo | **CONFIRMADO, no caminho legado do Desktop.** `FugaTES.runtime.json` ainda aponta para o remoto 5432. O monitor unificado aponta corretamente para 5433. |
| Divergência de schema | **NÃO.** Ambos usam `fugates`; a leitura unificada ainda valida identidade por `SqlIdentidadeAcm` e rejeita banco divergente (`UNIFIED_DEV_DATABASE_IDENTITY_REJECTED`). |
| Serviço recebendo no 5432 enquanto o monitor consulta 5433 | **NÃO É O CASO — porque não há serviço recebendo em nenhuma porta.** Ver causa-raiz. |
| Receptor SAP ausente ou desabilitado | **CONFIRMADO. Esta é a causa-raiz.** |
| Persistência H4 sem projeção contratual | **NÃO.** Projeção, H4 e IntegrationId são gravados na mesma transação, com falha dura se o H4 não for criado. |
| Filtro indevido na API de leitura | **RISCO REAL, por desenho.** `ContratosAcmPostgreSqlReadRepository.ConsultaSql` filtra `where payload_contrato->>'Centro' = @centro`, e `DevUnifiedReadStore` usa `selector ?? "3011"`. Contrato persistido com outro Centro, ou sem Centro no payload, **não aparece** sem passar o seletor. |
| Falha de atualização do monitor | **NÃO.** Atualização automática de 10 s e botão explícito presentes. |
| Divergência entre identidade do contrato e item | **NÃO na persistência** (`Item` vem da projeção; ausente fica nulo, nunca suposto). Observação: na leitura atual do 5433, cinco dos seis registros têm `item: null` e apenas `1147` traz `item: "10"`. |

---

## CAUSA-RAIZ

**Não existe nenhum receptor SAP publicado ou habilitado contra o banco DEV unificado.**

Três fatos independentes sustentam isso:

1. O único processo `FugaTES.Callback.Http` instalado na VM é o leitor:
   `DEV_READER_INSTALL_RESULT.json` → `{"host":"vm-fugatesbio","pid":634819,"listener":"127.0.0.1:18762","mode":"GET_ONLY_DEV_NO_WORKERS"}`,
   iniciado com `--dev-unified-read-only`. Nesse modo o `Program.cs` **retorna antes** de construir o host de
   entrada: a rota `POST /api/v1/integracoes/sap/contratos` nunca é mapeada.
2. `SapContractInbound:ProductiveEnabled = false` tanto no `appsettings.json` do repositório quanto no
   `appsettings.json` publicado do leitor DEV.
3. Ligar apenas essa chave **não** restabeleceria o fluxo. Com a configuração de referência
   (`AutomaticProcessingEnabled: true`, `AcmInboundWindow.Enabled: false`, `AcmPilot*: null`),
   `SapContractInboundProductiveComposition` lança **`SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED`**
   na partida, porque `MotorIntegracaoOptions.UsaEscopoAcm` é falso. E se o serviço subisse sem escopo
   automático, cada POST seria recusado com **422 `SAP_INBOUND_SCOPE_NOT_AUTHORIZED`**, já que o snapshot
   é comparado contra os campos-piloto nulos.

Ou seja: a recepção automática está, por desenho, atrás de uma **janela de admissão autorizada e
time-boxed** (`AcmInboundWindowOptions`), e nenhuma janela está configurada. Isso não é um defeito de
código — é uma decisão de configuração e autorização.

Consistente com o handoff: *“Current stage: read-only catalog and contract-lineage inventory; no migration,
data copy or application publication yet”* e *“Installed HML datasource/service bindings must not be changed.”*

### Natureza dos contratos hoje no 5433
A leitura autorizada de 27/10 devolveu 6 registros: `0000986801` classificado
`SIMULACAO_CONFIRMADA` (id piloto) e `0000001139`, `0000001140`, `0000001144`, `0000001155`, `1147/10`
classificados `PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA` por `DevReadScope.Classify`. **Nenhum deles
comprova um POST SAP real.** Não foram copiados nem tiveram origem atribuída por mim.

---

## Achados secundários (não corrigidos — arquivos em edição pelo Codex)

1. **Porta esperada desatualizada na mensagem de desconexão.**
   `src/FugaTES.WinForms/Monitor/PhysicalReadConnection.cs` exibe, quando desconectado,
   *“API privada localhost:**18761**”*, e `LOCAL_PORT_UNTRUSTED` diz *“Porta 18761 ocupada…”*. Mas
   `UnifiedDesktopSession` exige `RemoteReadPort == 18762` quando `UnifiedDatabaseReadback` é verdadeiro
   (o perfil instalado), e o leitor antigo do 18761 ficou `PRESERVED`. Correção mínima: derivar a porta
   do perfil em vez de fixá-la no texto. **Pertence a `Monitor/`, diretório novo do Codex — não alterado.**
2. **Lista de contratos históricos possivelmente incompleta.**
   `AcmInboundWindowOptions.HistoricalContracts` fixa 8 números (`1031, 1106, 1138, 1139, 1140, 1144,
   1147, 1155`), mas o gate referencia **14** contratos distintos no histórico 5432. Se forem realmente 14,
   seis deles não estão protegidos contra readmissão por uma janela futura. Não inventei os faltantes:
   requer a lista atestada pela Hera.
3. **Filtro por Centro.** Ver Missão 2. Qualquer contrato fora do Centro 3011 ficará invisível na grade
   sem seletor explícito.

---

## Missão 4 — Visibilidade no DEV110 (já atendida pelo código existente)

| Requisito | Onde é atendido |
|---|---|
| Número do contrato e item | `ContratoAcmLeitura.NumeroContrato` / `.Item` (`payload_contrato->>'Item'`) |
| JSON persistido | `.PayloadContratoJson` (`payload_contrato::text`) |
| Status de processamento | `payload_contrato->>'StatusContrato'` e `integracao.estado` |
| IntegrationId | `v.integracao_id` |
| Atualizar sem reiniciar | timer de 10 s + botão “Conectar / Atualizar leitura” |
| Diferenciar sintéticos | `DevReadScope.Classify` → `SIMULACAO_CONFIRMADA` vs `PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA`; `DevProtocolView.KnownSynthetic` |
| Falha de comunicação sem exibir dado antigo como atual | `PhysicalReadConnection.Failure` zera `Connected`, dá causa nominal e mantém “Última leitura física” separada do estado atual |

Nenhuma implementação nova foi necessária. Em particular, a atualização automática **já existe**, portanto
não há motivo para introduzir outro mecanismo periódico.

---

## Procedimento mínimo de restauração — PREPARADO, NÃO EXECUTADO

Requer autorização específica da Hera. Não executar nenhum passo sem ela.

1. **Decidir o destino empresarial.** Confirmar se o SAP/CPI deve passar a entregar no DEV unificado
   (`18/fugatesacmdev:5433`) ou permanecer no destino atual. Nada no código deve redirecionar o SAP/CPI
   automaticamente; é decisão de negócio.
2. **Publicar uma instância receptora** de `FugaTES.Callback.Http` na VM, **sem** `--dev-unified-read-only`,
   em porta própria (o repositório usa `http://127.0.0.1:18085`), preservando o leitor GET-only do 18762.
3. **Configuração do receptor** (derivada de `appsettings.inert.json`, sem inventar valores):
   - `ConnectionStrings:FugaTES` → `Port=5433; Database=fugates_dev; Username=fugates_acm_hml_app; Search Path=fugates`
   - `SapContractInbound:ProductiveEnabled = true`
   - `SapContractInbound:SecurityMode = LoopbackBasicHttps` e as credenciais `SapInbound:BasicAuth:*`
   - `SapContractInbound:AutomaticProcessingEnabled = true`
   - `HostIntegracao:PersistenceMode = PostgreSql`, `HostIntegracao:ExternalMode = Guardian`,
     `AcmR57:Enabled = true`, `AcmR57:Normal:Enabled = true`
4. **Definir o escopo de admissão** — sem isto o serviço não sobe. Uma das duas formas:
   - janela: `MotorIntegracao:AcmInboundWindow` com `Enabled`, `WindowId` (GUID “N”), `AuthorizationReference`,
     `NotBeforeUtc`/`UntilUtc` em UTC com duração ≤ 1 dia, `Centro = "3011"`, e `AllowedSapPartners` e
     `AllowedSapMaterials` **não vazios**; ou
   - piloto: `AcmPilotNumeroContrato` + `AcmPilotCentro` + `AcmPilotItem`.
5. **Privilégios PostgreSQL.** O role `fugates_acm_hml_app` precisa de escrita em `contrato_sap_toledo_*`
   e `integracao` no 5433. Hoje a evidência só comprova leitura. Verificar antes de habilitar — sem
   `GRANT` neste gate.
6. **Reverso do SAP/CPI.** Apontar o POST para o receptor publicado, via o proxy que fornece
   `X-Forwarded-Proto: https`, mantendo loopback e Basic auth.
7. **Comprovação ponta a ponta.** Só um **novo envio SAP real**, dentro da janela autorizada, comprova o
   fluxo completo. Depois: `GET /dev/read/contracts` deve trazer o contrato com JSON e `integrationId`, e a
   grade do DEV110 deve exibi-lo em até 10 s sem reinício.

---

## Build e testes (29 arquivos do Codex em voo nesta árvore)

`dotnet build FugaTES.slnx -c Release` → **êxito, 0 erros**, 3 avisos.

| Suíte | Aprovados | Falhas |
|---|---|---|
| FugaTES.Testes | 1569 | 1 |
| R164E.Integration.Tests | 35 | 0 |
| R165A.Tests | 43 | 0 |
| R165U.Automatic.Tests | 594 | 8 |
| FugaTES.WinForms.Monitor.R165M.Tests | 254 | 1 |
| R165G.TicketIdentity.Tests | 24 | 0 |
| R165R.Provider.Tests | 113 | 2 |
| **TOTAL** | **2632** | **12** |

### Drift concorrente observado DURANTE este gate

No início do diagnóstico, `UnifiedDesktopSession` declarava
`Version="1.2.3"` / `Release="DEV110-DEDICATED-RETRY-COMMAND-20261028-R1"` — a baseline nomeada pelo gate.
Ao final, o mesmo arquivo declara
`Version="1.2.4"` / `Release="DEV110-SHARED-CONTRACT-PROTOCOL-RETRY-20261028-R1"`.

O Codex avançou a versão enquanto este diagnóstico corria. Isso, somado à republicação de
`C:\FugaTES_DEV110` às 08:46 de hoje, é motivo suficiente para **não publicar nada** neste gate:
qualquer build meu sobrescreveria alteração concorrente, o que o gate proíbe. A fonte já não está mais na
baseline 1.2.3, de modo que uma publicação “compatível com a baseline” não seria verdadeira.

### Falhas de teste

As 12 falhas são de perfis empacotados, paridade do binário instalado, entrypoint compilado, paridade REST
do motor Guardian, preparação do piloto 1140 e relógio de polling — todas sobre **artefatos publicados**, não
sobre a cadeia recepção → persistência → leitura. São o estado corrente da árvore do Codex, que republicou
`C:\FugaTES_DEV110` hoje às 08:46 enquanto o fonte continua mudando. Não as alterei: os arquivos pertencem
ao trabalho em voo do Codex e o gate proíbe sobrescrever alterações concorrentes.
