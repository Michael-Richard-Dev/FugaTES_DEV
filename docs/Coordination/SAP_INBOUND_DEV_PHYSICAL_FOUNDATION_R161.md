# R161 — Fundação física SAP no DEV unificado

Gate `FUGATES-SAP-INBOUND-DEV-PHYSICAL-FOUNDATION`. Alvo `18/fugatesacmdev / fugates_dev / 5433`.
`BUSINESS_DATABASE_WRITES=0` · `EXTERNAL_POSTS=0` · `SAP_RECEIVER_ACTIVE=NO` · `R57_WINDOW_ACTIVE=NO`.

---

## Missão A — API privada 18762 atualizada  `PASS`

### Preflight
| Verificação | Resultado |
|---|---|
| Host / usuário | `vm-fugatesbio` / `michael` |
| Processo em 18762 | PID **634819**, `…/20261027_ECF854604610/FugaTES.Callback.Http.dll --dev-unified-read-only` — idêntico ao `DEV_READER_INSTALL_RESULT.json` |
| Diretório pai | existe, não é link, modo `700`, dono `michael` |
| Pacote remoto já presente | não |
| 18761 / 18763 | PIDs 621770 e 651989 — **outros serviços da VM**, não tocados |

### Backup
`/home/michael/.fugates-unified-dev-read/BACKUP_20261027_ECF854604610.tar.gz`
1 215 787 bytes, SHA-256 `6c375ad8c277af5b370b9aeb8eb6709859f057156211ba578ed04708d2b6603e`.
O diretório de instalação anterior também permanece intacto — é o ponto de rollback.

### Publicação
Pacote `FUGATES_UNIFIED_DEV_READ_API_TODOS_R2.zip`, SHA-256
`836A9D58826E1460E3CB8589B92D98A2484C2BBFAA4CE9FCF7605A0D3F27D703` — **hash remoto idêntico** após
o upload. Extraído em diretório novo `/home/michael/.fugates-unified-dev-read/20261028_144029_TODOS_R2`,
com **17/17 arquivos conferidos contra o `MANIFEST.json`** (hash e tamanho), `mode=--dev-unified-read-only`,
`port=18762`, `databasePort=5433`.

Troca com rollback automático embutido: processo antigo encerrado, porta liberada, novo iniciado com
as **mesmas** variáveis do anterior (`FUGATES_DEV_READ_APPROVAL`, `FUGATES_DEV_READ_KEY_FILE`,
`R165AL_APPROVED_PASSFILE`), e verificação imediata. Resultado:

```
velho_pid=634819  →  novo_pid=657305
identity_http=200
{"cluster":"18/fugatesacmdev","port":5433,"database":"fugates_dev",
 "role":"fugates_acm_hml_app","readOnly":true}
```

### Validação do seletor TODOS  `ALL_CENTERS=PASS`

```
default (3011)          : http=200  total=6
seletor TODOS           : http=200  total=6
centro 9999 (controle)  : http=200  total=0
```

O controle é a prova: `9999` devolve **0**, logo o filtro continua ativo e `TODOS` não é um
"ignorar sempre". Antes desta atualização, `TODOS` era tratado como centro literal e devolveria 0
como o 9999. Hoje os seis contratos pertencem todos ao centro 3011, então os totais coincidem —
a diferença aparecerá quando existir contrato de outro centro.

### Contratos e JSON  `DEV110_READBACK=PASS`

Os seis contratos existentes, com natureza por registro:

| Contrato | Item | Centro | Versão | Natureza |
|---|---|---|---|---|
| 0000986801 | — | 3011 | 1 | SIMULACAO_CONFIRMADA |
| 0000001139 / 1140 / 1144 / 1155 | — | 3011 | 1 | PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA |
| 1147 | **10** | 3011 | 1 | PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA |

JSON persistido do **1147 / item 10**: presente e válido, 25 campos —
`NumeroContrato=1147`, `Item=10`, `Centro=3011`, `TipoContrato=AC3P`, `StatusContrato=T`,
`Material=7000000`, `IdParceiro=0020000051`. IntegrationId `54c4b8eb-c6c5-460c-ab47-9cb2054e5550`.

### Contrato da API preservado

```
POST /api/v1/integracoes/sap/contratos  →  404   (rota de recepção não mapeada)
POST /dev/read/contracts                →  405   (GET-only)
cmdline: … /20261028_144029_TODOS_R2/FugaTES.Callback.Http.dll --dev-unified-read-only
listeners: 127.0.0.1:18761, 127.0.0.1:18762, 127.0.0.1:18763  (os três preservados)
```

Nenhuma rota POST, nenhum worker, nenhuma flag adicional.

### Fechamento e reabertura do DEV110  `PASS`

Fechado e reaberto contra a API nova: v1.2.4, `Connected=True`,
`18/fugatesacmdev | 5433 | fugates_dev | fugates_acm_hml_app`, 6 contratos,
JSON “Contrato 1147 / item 10” válido, `sendEnabled=False`. 21 arquivos de evidência.
Encerrado em seguida pelo script dedicado (`SSH_TUNNEL_CLEANUP=NOT_REQUIRED`).

---

## Missão B — Identidade de escrita  `BLOCKED`

Interrompida por **dois motivos independentes**, ambos previstos no gate.

### B.1 — Delta de privilégios não previsto (motivo principal)

O gate manda: *"Se houver privilégios adicionais não previstos, interromper e devolver o delta."*
A verificação somente leitura encontrou o seguinte, que **contradiz o que eu mesmo afirmei em
R159 e R160**: concluí naquele momento que `fugates_acm_hml_app` era somente leitura, com base no
cabeçalho da migração de 27/10 (*"No writer grants"*). **O catálogo diz outra coisa.** A conclusão
anterior estava errada e a correção é esta:

```
contrato_sap_toledo_identidade : fugates_acm_hml_app = ar    → INSERT + SELECT
contrato_sap_toledo_versao     : fugates_acm_hml_app = ar    → INSERT + SELECT
integracao                     : fugates_acm_hml_app = a     → INSERT
```

E, por coluna, em `fugates.integracao`, a role leitora tem **SELECT nas 17 colunas** e
**UPDATE em 9**: `estado`, `tipo_disparo_pendente`, `proxima_tentativa_em`, `fencing_token`,
`worker_proprietario`, `lease_expira_em`, `fase_envio`, `tentativa_atual_id`, `atualizado_em`.

Consequências que a Hera precisa decidir antes de qualquer `CREATE ROLE`:

1. **`SAP_READER_ISOLATION=BLOCKED`.** A role que serve a API GET-only já pode inserir nas três
   tabelas do receptor. Hoje nada é escrito porque a API abre conexões com
   `default_transaction_read_only=on` — a proteção é de **sessão**, não de privilégio.
2. `UPDATE(estado)` é privilégio de **worker**: permite disparar o trigger
   `trg_h4_guard_claim_contrato_sap_toledo` e mover integrações para `EM_PROCESSAMENTO`.
3. Criar `fugates_sap_inbound_app` com privilégio mínimo **não produz isolamento** enquanto a
   leitora mantiver esses grants. Isolar exige um `REVOKE` na leitora — que é alteração de
   privilégio de role existente, fora do que este gate autoriza, e que precisa ser avaliada contra
   quem mais usa essa identidade.

Observação técnica relacionada: `has_table_privilege(...,'integracao','SELECT')` é **falso** para a
leitora, mas a leitura funciona porque os grants ali são **por coluna** — é o resultado esperado,
não uma inconsistência.

### B.2 — Autenticação privilegiada indisponível a mim

```
sudo -n true                  → sudo: a password is required
sudo -n -u postgres psql      → sudo: a password is required
psql local como michael       → pg_hba.conf rejects connection
```

O único acesso que tenho ao 5433 é a role leitora, que é `rolsuper=f` e sem `CREATEROLE`.
`CREATE ROLE`/`GRANT` exigem `postgres` via `sudo`, cuja senha deve ser digitada pelo operador no
próprio terminal — não a capturo nem a transporto.

Confirmado também: `fugates_sap_inbound_app` **não existe** (0 em `pg_roles`) e **não há
sequências** nas tabelas do receptor (0), validando a ACL proposta em R160.

### Artefatos entregues, prontos para execução autorizada

| Arquivo | Papel |
|---|---|
| `docs/Incrementos/UnifiedDev/004_SAP_INBOUND_WRITER_ROLE_DEV.sql` | `CREATE ROLE` + os grants mínimos do R160, com guarda de destino (cluster/porta/base/recovery/owner/`session_user=postgres`) e **postcheck em quatro frentes**: completude, **excesso** (recusa DELETE/TRUNCATE/UPDATE irrestrito e qualquer privilégio em `tentativa_integracao`/`auditoria_integracao`), `UPDATE` restrito a `ultima_versao`, e role não privilegiada |
| `…/004_ROLLBACK_SAP_INBOUND_WRITER_ROLE_DEV.sql` | revoga exatamente o concedido e remove a role; confirma que a leitora não foi afetada |
| `deploy/sap-inbound-dev/Criar-RoleEscritaSap.ps1` | executor interativo: exige backup existente, gera a senha **na VM** (`openssl rand`), grava em pgpass `600` e **nunca a exibe**, transporta o SQL com verificação de hash, e imprime recibo sanitizado |

A senha nunca aparece em código, log, pacote ou nesta documentação.

---

## Missão C — Receptor SAP  `SAP_RECEIVER_CONFIG=READY`

`deploy/sap-inbound-dev/appsettings.receiver.dev.json` finalizado com destino exclusivo
`Port=5433 / Database=fugates_dev / Search Path=fugates` e `Username=fugates_sap_inbound_app`.
Não instalado, não iniciado, não exposto.

Estado da validação — fail-closed por construção:

```
TARGET_DATABASE=fugates_dev:5433   WINDOW_ENABLED=False   WORKERS_ENABLED=False
RECEIVER_PACKAGE=BLOCKED
  - pendencias nao preenchidas: … - conexao sem Passfile absoluto
  - escopo ACM ausente -> SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED
```

Campos registrados como **pendentes**, nenhum inventado:

| Campo | Chave |
|---|---|
| Porta corporativa de entrada | `Urls` |
| URL pública / rota de ingresso | externa ao arquivo |
| Identidade SAP/CPI de origem | externa ao arquivo |
| Credenciais Basic | `SapInbound:BasicAuth:Username` / referência de segredo |
| Passfile da role de escrita | `ConnectionStrings:FugaTES` |
| Partners e Materials autorizados | `AcmInboundWindow.AllowedSapPartners` / `AllowedSapMaterials` |
| Janela R57 | `WindowId`, `AuthorizationReference`, `NotBeforeUtc`, `UntilUtc` |

---

## Preservação

`FugaTES.exe` v1.2.4 intacto. Build Release do repositório: **0 erros** (nenhum fonte alterado
neste gate — só documentos, SQL e scripts). 18761 e 18763 preservados. Nenhuma escrita de negócio,
nenhum POST externo, nenhuma janela aberta, nenhum worker ativado, nada tocado em 5432, 5434, HML
ou produção.
