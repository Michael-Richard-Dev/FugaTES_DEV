# R160 — Preparação da ativação física do receptor SAP

Gate `FUGATES-SAP-INBOUND-PHYSICAL-ACTIVATION-PREPARATION`. Baseline `DEV110_1.2.4`.
`REAL_SAP_POSTS=0` · `REAL_GUARDIAN_POSTS=0`. Nenhum `CREATE ROLE`, `GRANT`, DDL, deploy
empresarial, abertura de janela ou POST real.

---

## 1–2. Expectativa de versão corrigida

`VERSION_TEST=PASS` — `AcmMonitorStartupTests` reexecutado: **255/255** na suíte (antes 254/1).

A finalidade do teste é garantir que o **artefato compilado corresponde à release declarada**. Em
vez de trocar um literal defasado (1.2.3) por outro literal que defasaria na próxima promoção, a
expectativa passou a ser ancorada em `UnifiedDesktopSession.Version` — a versão que o Desktop
exibe e que a Hera homologa — mais uma asserção explícita de que essa constante é a baseline
comprovada **1.2.4**:

```csharp
var declarada = Version.Parse(UnifiedDesktopSession.Version);
Assert.Equal(new Version(declarada.Major, declarada.Minor, declarada.Build, 0), assembly.GetName().Version);
Assert.Equal($"{…}.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
Assert.Equal("1.2.4", UnifiedDesktopSession.Version);
```

Divergência entre binário e versão declarada continua falhando; promover a versão num único lugar
mantém o teste coerente. Nenhuma outra asserção do teste foi tocada.

---

## 3. API GET-only 18762 — atualização controlada preparada

`READ_API_18762_RELEASE=READY` (aguardando autorização física; **nada publicado**)

Pacote reproduzível `FUGATES_UNIFIED_DEV_READ_API_TODOS_R2.zip`
SHA-256 `836A9D58826E1460E3CB8589B92D98A2484C2BBFAA4CE9FCF7605A0D3F27D703`, 17 arquivos +
manifesto, construído pela fonte canônica com o `NETSDK1152` já resolvido.

Script `deploy/dev-read-api-todos/Publicar-ApiLeitura18762.ps1` — **bloqueado por padrão**.
Sem `-AutorizacaoHera` ele valida e imprime o plano, sem enviar nada. Validações que executa:

- SHA-256 do pacote contra o esperado (recusa comprovada com o pacote anterior);
- presença de `MANIFEST.json`, modo `--dev-unified-read-only`, porta 18762, banco 5433;
- ausência de `appsettings*.json` (o modo GET-only não os usa);
- varredura por credenciais (`KEY`, `BEARER`, `pgpass`, `.pem`, `id_rsa`, `id_ed25519`).

Saída do ensaio atual:

```
MODO=--dev-unified-read-only  PORTA=18762  BANCO=5433   ARQUIVOS=17
ROLLBACK_PARA=/home/michael/.fugates-unified-dev-read/20261027_ECF854604610
TUNEL_18761=NAO_UTILIZADO_POR_ESTE_SCRIPT      SERVICO_COMANDOS_18763=PRESERVADO
READ_API_18762_RELEASE=READY_AWAITING_HERA_AUTHORIZATION
```

**Rollback preservado:** a instalação de 27/10 permanece intacta; a nova vai para diretório novo
com carimbo de data, e o PID anterior só é encerrado **após** a confirmação do novo listener.
Reverter é reiniciar a instalação anterior e encerrar o PID novo — nada é removido.

Mesmo autorizado, o script **não publica sozinho**: a transferência e a troca de processo exigem o
terminal do operador, com a chave SSH digitada somente lá.

---

## 4. Role de escrita — conjunto completo verificado

`SAP_WRITER_ROLE=READY` (proposta; a role **não existe** no servidor)

Verifiquei o DDL real (`MIGRATION_006H4_CONTRATO_SAP_TOLEDO_VERSOES.sql`) e o código da transação.

### Triggers e objetos dependentes

- **Único trigger** nessas tabelas: `trg_h4_guard_claim_contrato_sap_toledo`, que é
  **`BEFORE UPDATE OF estado ON fugates.integracao`**. Dispara apenas na transição para
  `EM_PROCESSAMENTO`, que é do **worker** — o receptor só faz `INSERT` em `integracao`, então o
  trigger **nunca** entra no caminho de recebimento. A função é `SECURITY DEFINER` com `EXECUTE`
  concedido a `fugates_dev_app`; a role de escrita **não** precisa desse `EXECUTE`.
- **Sequências: nenhuma.** As chaves são `text`, composta e `uuid`; os defaults são
  `clock_timestamp()` e `0`. Portanto **nenhum `USAGE` de sequência é necessário** — corrijo aqui
  a minha proposta anterior, que o previa condicionalmente.
- **Chaves estrangeiras:** `contrato_sap_toledo_versao` referencia `identidade` e `integracao`. A
  verificação de FK roda por triggers internos do sistema, com privilégio da constraint, não do
  usuário do DML — **nenhum `REFERENCES` em runtime**.
- **Caminho de admissão (janela R57 ativa):** `AdmittedRowsAsync` executa
  `select i.id, i.correlation_id, i.criado_em, i.origem, i.destino, i.operacao, i.chave_negocio,
  i.fingerprint_sha256 from integracao i left join contrato_sap_toledo_versao v …`.
  Logo o receptor **também lê `integracao`** — o que a minha proposta anterior não previa.
- `pg_advisory_xact_lock` é executável por `PUBLIC`; nenhuma concessão adicional.

### `SAP_WRITER_ACL_SCOPE` — conjunto exato e mínimo

```sql
GRANT USAGE  ON SCHEMA fugates                              TO fugates_sap_inbound_app;
GRANT SELECT, INSERT ON fugates.contrato_sap_toledo_identidade TO fugates_sap_inbound_app;
GRANT UPDATE (ultima_versao) ON fugates.contrato_sap_toledo_identidade TO fugates_sap_inbound_app;
GRANT SELECT, INSERT ON fugates.contrato_sap_toledo_versao  TO fugates_sap_inbound_app;
GRANT INSERT ON fugates.integracao                          TO fugates_sap_inbound_app;
GRANT SELECT (id, correlation_id, criado_em, origem, destino,
              operacao, chave_negocio, fingerprint_sha256)
      ON fugates.integracao                                 TO fugates_sap_inbound_app;
```

Isso espelha exatamente a ACL de runtime que a própria migração já aplica a `fugates_dev_app`
(`GRANT SELECT, INSERT` + `GRANT UPDATE (ultima_versao)`, com o comentário *"No table-wide UPDATE
grant is given"*) — portanto não é um modelo novo, é o modelo já homologado, aplicado a uma
identidade separada.

**Fora do escopo, deliberadamente:** `DELETE`, `TRUNCATE`, qualquer privilégio em
`tentativa_integracao` e `auditoria_integracao` (são do worker, desabilitado), `EXECUTE` em
funções, `REFERENCES`, `DEFAULT PRIVILEGES`, e qualquer participação em `fugates_dev_owner`.

**A role leitora `fugates_acm_hml_app` permanece somente leitura.** Ela serve a API 18762, que
abre conexões `read_only`; conceder escrita a ela daria, no catálogo, capacidade de gravação a um
componente que só lê.

---

## 5. Guardas de conexão estendidas à role nova

`NpgsqlDataSourceFactory` passou a reconhecer duas identidades guardadas:

```csharp
public const string SapInboundApplicationRole = "fugates_sap_inbound_app";
public static bool EhRoleGuardada(string? username)
    => username == AcmApplicationRole || username == SapInboundApplicationRole;
```

Com isso a role de escrita herda **as mesmas** guardas da leitora: porta 5433, `fugates_dev`,
schema `fugates`, host loopback, `Passfile` absoluto, `PGPASSWORD` ausente, e verificação de
identidade física a cada conexão. `Criar()` roteia ambas para `CriarAcm`, de modo que o caminho
genérico não serve de contorno.

`AcmPostgreSqlIdentityGuard.VerifyAsync` ganhou o parâmetro opcional `expectedRole` (terceiro, para
não quebrar os chamadores posicionais existentes) e passou a exigir `current_user` e `session_user`
iguais à role da própria connection string — recusando qualquer identidade fora da lista guardada.
Cluster, porta, base, schema e recovery continuam fixos.

Provado por 45 testes em `R158.SapInboundReadiness.Tests`, entre eles: destino divergente recusado
para **ambas** as roles (5432, 5434, 15434, base e schema errados, host remoto), `Passfile`
obrigatório para ambas, caminho genérico sem contorno, e sonda recusando role não guardada.

---

## 6. Receptor SAP isolado

`SAP_RECEIVER_RELEASE=READY` · `R57_SCOPE=READY` (estrutura; janela **não criada**)

`deploy/sap-inbound-dev/appsettings.receiver.dev.json` atualizado para a identidade de escrita:

```
Username=fugates_sap_inbound_app   Port=5433   Database=fugates_dev   Search Path=fugates
Passfile=<<PENDENTE_PASSFILE_ABSOLUTO_APROVADO_HERA>>
FUGATES_INTEGRATION_WORKER_ENABLED=false   AcmR57.Normal.ExecutionAuthorized=false
AcmInboundWindow.Enabled=false             Callback.Enabled=false
```

Validação atual (fail-closed, como deve ser):

```
TARGET_DATABASE=fugates_dev:5433   WINDOW_ENABLED=False   WORKERS_ENABLED=False
RECEIVER_PACKAGE=BLOCKED
  - pendencias nao preenchidas: <<PENDENTE_PASSFILE…>>, <<PENDENTE_PORTA…>>, <<PENDENTE_WINDOWID…>>, …
  - conexao sem Passfile absoluto
  - escopo ACM ausente -> SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED
```

---

## 7. `SAP_CPI_EXTERNAL_DEPENDENCY` — o que ainda depende de confirmação externa

O endpoint autentica por **loopback + Basic + `X-Forwarded-Proto: https`**. Não há verificação de
certificado de cliente nem de identificador de origem no código — a identidade da origem é
estabelecida pelo par de credenciais e pelo proxy que termina o TLS. Logo, falta confirmar:

| # | Item | Quem confirma |
|---|---|---|
| 1 | Porta loopback do listener do receptor (≠ 18761/18762/18763) | Hera |
| 2 | URL/rota corporativa de ingresso que o SAP/CPI chamará, e o proxy reverso que termina o TLS e injeta `X-Forwarded-Proto: https` | Infraestrutura + Hera |
| 3 | Identidade da origem SAP/CPI: tenant/sistema emissor e o endereço com que ele chegará ao proxy | SAP/CPI |
| 4 | `SapInbound:BasicAuth:Username` e a referência do segredo (nunca senha em claro no arquivo) | Hera |
| 5 | `Passfile` absoluto da role de escrita no host | Operador/Hera |
| 6 | Janela R57: `WindowId` (GUID `N`), `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` (≤ 1 dia) | Hera |
| 7 | `AllowedSapPartners` e `AllowedSapMaterials` — hoje **vazios**, e vazio reprova a janela | SAP/negócio |
| 8 | `Item` da janela, se o recebimento for restrito a um item | SAP/negócio |

`Centro` já é fixo em `3011` pela própria validação da janela, e os 8 contratos históricos do 5432
continuam excluídos por construção.

---

## 8. Ciclo de vida do túnel SSH

`SSH_TUNNEL_LIFECYCLE=PASS`

**Correção do meu relato anterior:** eu atribuí a falha de reabertura do R159B a um defeito de
ciclo de vida. Revendo o código, `LocalReadTunnel.Dispose()` já mata **apenas** o processo que ele
próprio iniciou (`owned`), preservando explicitamente túneis de terceiros, e
`UnifiedDesktopSession.Dispose()` o descarta — e `UnifiedDesktopStartup` usa `using`. Num
encerramento normal não sobra listener. O resíduo do R159B foi causado pelo **meu próprio
`Stop-Process -Force`** no harness, que pula `using`/`finally` e deixa o `ssh` filho órfão.

O risco real que resta é o mesmo: travamento ou finalização forçada deixa um órfão. Para isso,
`deploy/sap-inbound-dev/Encerrar-TunelRemanescente.ps1` encerra o órfão **e somente ele**, exigindo
três critérios cumulativos:

1. ser o **único** listener de `127.0.0.1:18761`;
2. `ExecutablePath` igual ao OpenSSH do Windows;
3. linha de comando com o encaminhamento exato `-L 127.0.0.1:18761:127.0.0.1:<porta do perfil>`
   **e** o `IdentityFile` do perfil.

Qualquer divergência ⇒ `SSH_TUNNEL_CLEANUP=REFUSED_NOT_FUGATES`, sem encerrar nada. Se o FugaTES
estiver rodando ⇒ `SKIPPED_APP_RUNNING`. Não há `Get-Process ssh` nem `Stop-Process -Name`:
é impossível atingir um `ssh` de terceiros. Verificado no estado atual:
`SSH_TUNNEL_CLEANUP=NOT_REQUIRED`.

O contrato de propriedade e a recusa de comandos estranhos estão cobertos por testes (executável
diferente, porta remota diferente, argumento extra, identidade diferente — todos recusados).

---

## Build e testes

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 45 | 0 |
| FugaTES.Testes | 1570 | 0 |
| R164E.Integration.Tests | 35 | 0 |
| R165A.Tests | 43 | 0 |
| R165U.Automatic.Tests | 602 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 255 | 0 |
| R165G.TicketIdentity.Tests | 24 | 0 |
| R165R.Provider.Tests | 115 | 0 |
| FugaTES.Masterdata.R28.Tests | 17 | 0 |
| FugaTES.CarrierBinding.R37.Tests | 32 | 0 |
| FugaTES.SingleCreate.R31 / R41 | 128 | 0 |
| **TOTAL** | **2866** | **0** |

Suíte inteira verde. As 12 falhas do histórico estão encerradas: 10 eram ambiente (variáveis
apontadas para os artefatos que já existiam), 1 era instabilidade de tempo e 1 era a expectativa
de versão, agora ancorada na release declarada. Nenhuma expectativa foi relaxada ou removida —
a única alterada foi a de versão, por determinação explícita deste gate, preservando a finalidade
do teste.

## Preservação

`FugaTES.exe` v1.2.4 e os 33 arquivos do DEV110 intactos — **nenhuma publicação** nesta etapa.
API 18762 instalada não substituída; túnel 18761 e serviço de comandos 18763 preservados. A role
de escrita **não foi criada** e nenhum `GRANT` foi executado.
