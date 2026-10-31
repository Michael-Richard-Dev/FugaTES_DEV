# R162 — Isolamento de roles PostgreSQL do SAP no DEV

Gate `FUGATES-R162-SAP-POSTGRESQL-ROLE-ISOLATION`. Alvo `18/fugatesacmdev / fugates_dev / 5433`.
`DATABASE_CHANGES=0` · `EXTERNAL_POSTS=0` · `RECEIVER_ACTIVE=NO`.
Nenhum `REVOKE`, `CREATE ROLE` ou `GRANT` executado; serviço 18762 inalterado.

---

## 1. Consumidores de `fugates_acm_hml_app`  `IDENTIFIED`

Levantado em catálogo, processos da VM e configuração instalada.

| Serviço | Porta | Processo | Identidade | Escreve? |
|---|---|---|---|---|
| API privada de leitura DEV | **18762** | PID 657305, `--dev-unified-read-only` | `fugates_acm_hml_app` (`R165AL_APPROVED_PASSFILE`) | não — sessão `read_only` |
| Readback R165AL | **18761** | PID 621770, `R165AL.Runner readback` | `fugates_acm_hml_app` (mesmo passfile) | não observado |
| Serviço de comandos ACM | **18763** | PID 651989, `--acm-manual-retry-command` | `fugates_acm_hml_app` (mesmo passfile, via `profile.json`) | **sim** |
| Callback HML | 8080 | PID 474052, usuário `fugates-http` | não legível (outro usuário) | — |
| HostIntegracao inerte / leitor de erros | — / 18085 | PIDs 564527 / 583632 | sem passfile; `PersistenceMode=Fake` | não |

Fato decisivo do catálogo: **`fugates_acm_hml_app` é a única role com `LOGIN` além de `postgres`.**
`fugates_dev_owner` e `fugates_dev_app` têm `rolcanlogin=f`. Toda aplicação que se conecta ao
`fugates_dev` usa essa identidade.

O serviço 18763 executa, no código (`AcmManualRetryPostgreSql`):
`update fugates.integracao set …`, `insert into fugates.auditoria_integracao …`,
`update fugates.projecao_resultado_fluxo_mvp set …` — e tem exatamente esses privilégios.

## 2. Origem dos privilégios excedentes  `IDENTIFIED`

- **Concessões diretas**, não herança. `pg_auth_members` mostra que `fugates_acm_hml_app` não é
  membro de role alguma (as únicas linhas são `pg_monitor` → papéis internos), e a própria role
  tem `rolinherit=f`. A ACL registra o concedente: `…=ar/fugates_dev_owner`.
- **Não vêm das migrações do repositório.** `001`, `002` e `003` em `docs/Incrementos/UnifiedDev`
  concedem apenas `USAGE` e `SELECT` (a de 27/10 declara “No writer grants” e adicionou 12
  `SELECT` por coluna). Os `INSERT`/`UPDATE` são anteriores a elas ou aplicados fora do
  repositório.
- **Não são resíduo acidental: são o conjunto operacional de que o 18763 depende.**

Inventário completo (8 tabelas com algum privilégio):

| Objeto | SELECT | INSERT | UPDATE | DELETE |
|---|---|---|---|---|
| `auditoria_integracao` | 8/9 colunas | **t** | — | f |
| `contrato_sap_toledo_identidade` | t | **t** | f | f |
| `contrato_sap_toledo_versao` | t | **t** | f | f |
| `correlacao_guardian_evento` | t | **t** | f | f |
| `correlacao_guardian_sap` | t | **t** | **t** | f |
| `integracao` | 17/17 colunas | **t** | **9/17 colunas** (inclui `estado`) | f |
| `projecao_resultado_fluxo_mvp` | t | **t** | **t** | f |
| `masterdata.carrier_single_create_journal` | t | f | f | f |

`UPDATE(estado)` em `integracao` é privilégio de worker: permite disparar o trigger
`trg_h4_guard_claim_contrato_sap_toledo` e mover integrações para `EM_PROCESSAMENTO`.

## 3. Comparação A × B  →  **`READER_ISOLATION_STRATEGY=B`**

| | A — revogar da role existente | B — nova identidade GET-only |
|---|---|---|
| Efeito no 18762 | isolado | isolado |
| Efeito no **18763** | **quebra**: perde `update integracao`, `insert auditoria_integracao`, `update projecao_resultado_fluxo_mvp` | nenhum |
| Efeito no 18761 | risco não medido (mesma role) | nenhum |
| Reversibilidade | regrant manual, sem inventário autoritativo da origem | `DROP ROLE` + `REVOKE` do que foi concedido |
| Superfície de risco | três serviços | um serviço |

O gate manda preferir B diante de qualquer compartilhamento operacional relevante — e o
compartilhamento é **comprovado e necessário**. **B.** A role compartilhada permanece intacta.

## 4. ACL da identidade GET-only  `READER_ROLE_ACL=READY`

`fugates_dev_read_app` — `docs/Incrementos/UnifiedDev/005_DEV_READ_ONLY_ROLE.sql` (+ rollback).

A ACL **espelha exatamente os `SELECT` que a role atual já possui e que a API comprovadamente
usa** — capturados do catálogo, não deduzidos:

- `SELECT` de tabela: `contrato_sap_toledo_identidade`, `contrato_sap_toledo_versao`,
  `correlacao_guardian_evento`, `correlacao_guardian_sap`, `projecao_resultado_fluxo_mvp`,
  `masterdata.carrier_single_create_journal`
- `SELECT` por coluna: `integracao` (17), `tentativa_integracao` (9), `auditoria_integracao` (8),
  `pausa_processamento` (4)
- `USAGE` em `fugates` e `masterdata`

Zero `INSERT`/`UPDATE`/`DELETE`/`TRUNCATE`. Reforço adicional:
`ALTER ROLE … SET default_transaction_read_only = on`, de modo que a sessão é somente leitura
mesmo que um privilégio seja concedido por engano no futuro.

Postcheck aborta se: houver **qualquer** privilégio de escrita nos dois schemas; faltar algum
`SELECT` que a API usa; a role for privilegiada ou membro do owner; **ou a role compartilhada for
afetada**.

## 5. ACL da role de escrita SAP  `SAP_WRITER_ROLE_ACL=READY`

`fugates_sap_inbound_app` preservada como aprovado em R160/R161 — `004_SAP_INBOUND_WRITER_ROLE_DEV.sql`:
`USAGE`; `contrato_sap_toledo_identidade` `SELECT, INSERT, UPDATE(ultima_versao)`;
`contrato_sap_toledo_versao` `SELECT, INSERT`; `integracao` `INSERT` + `SELECT` nas 8 colunas da
admissão. Sem direitos de worker, sem `tentativa_integracao`/`auditoria_integracao`, sem
`DELETE`/`TRUNCATE`.

## 6. Guardas Npgsql  `compatíveis para as três identidades`

`NpgsqlDataSourceFactory.EhRoleGuardada` passou a reconhecer também `fugates_dev_read_app`. Sem
isso a API 18762, ao ser repontada, seria recusada com `R165R_ACM_CONNECTION_METADATA_REQUIRED`.
As três identidades passam pelas mesmas guardas: porta 5433, `fugates_dev`, schema `fugates`,
host loopback, `Passfile` absoluto, `PGPASSWORD` ausente e verificação de identidade física por
conexão (`current_user`/`session_user` conferidos contra a role da connection string).

Testes: `R158.SapInboundReadiness.Tests` **47/47**, com as teorias de destino divergente e
`Passfile` obrigatório estendidas às três roles. Build Release **0 erros**; suítes afetadas
verdes (`FugaTES.Testes` 1570, `Monitor.R165M` 255, `R165R.Provider` 115).

## 7. Rota SAP/CPI  `SAP_CPI_ROUTE_EVIDENCE=FOUND`

**A rota já existe e está instalada** — não é preciso pedir definição nova para a maior parte.
`nginx` ativo, `/etc/nginx/sites-available/fugates-hml-http.conf` (habilitada por symlink):

```nginx
listen 192.168.17.40:80;  listen 127.0.0.1:80;
location = /api/v1/integracoes/sap/contratos {
    allow 10.200.3.77; allow 10.200.2.17; allow 192.168.3.173;
    allow 10.30.3.2;   allow 192.168.254.101/32;
    limit_except GET { … deny all; }   deny all;
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header X-Forwarded-For $remote_addr;   # não confia no header do cliente
    proxy_set_header X-Forwarded-Proto http;
    proxy_set_header Authorization "";
}
location / { return 404; }
```

Reutilizável de imediato:

| Item antes “pendente” | Evidência encontrada |
|---|---|
| URL de ingresso | `http://192.168.17.40/api/v1/integracoes/sap/contratos` |
| Identidade de origem SAP/CPI | allowlist por IP: `10.200.3.77`, `10.200.2.17`, `192.168.3.173`, `10.30.3.2`, `192.168.254.101/32` |
| Porta do backend | `127.0.0.1:8080` |
| Proteção adicional | `fugates-http80-firewall.service` (filtro de origem na porta 80) |

**Três incompatibilidades que a Hera precisa decidir** — nenhuma delas resolvível dentro deste gate:

1. `X-Forwarded-Proto: http`. O receptor em `SecurityMode=LoopbackBasicHttps` **exige `https`** e
   devolveria **403**.
2. `proxy_set_header Authorization "";` — o ingress **remove** o header. O endpoint exige Basic
   em ambos os modos de segurança, logo devolveria **401**.
3. O backend `127.0.0.1:8080` é o serviço **HML** `fugates-callback-http` (usuário `fugates-http`,
   release `R159B_8B235C675CED`). Apontar essa rota para um receptor DEV seria alteração de
   serviço HML — proibido por `NO_HML_SERVICE_CHANGES`. O receptor DEV precisa de **rota e porta
   próprias**.

Continua pendente de definição externa apenas: porta/rota dedicadas ao DEV, credencial Basic (ou
decisão de dispensá-la com um ingress que não remova o header), e os `AllowedSapPartners` /
`AllowedSapMaterials` e a janela R57.

## 8. Procedimento único para o administrador  `ADMIN_EXECUTION_REQUIREMENTS`

`deploy/sap-inbound-dev/PROCEDIMENTO_ADMIN_IDENTIDADES_DEV.md` — cinco passos: backup verificável,
criação da identidade GET-only, criação da identidade de escrita, repontar a API 18762, conferência
final com a tabela de privilégios esperada. Mais o rollback em ordem inversa.

Exigências: `sudo` na VM (senha digitada **apenas** no terminal do administrador), backup
confirmado antes de qualquer DDL, senhas **geradas na VM** com `openssl rand` e gravadas em pgpass
`600`, nunca exibidas nem transmitidas. Alternativa equivalente a partir do Windows:
`Criar-RoleEscritaSap.ps1`.

---

## Achado de segurança, independente deste gate

O serviço 18763 expõe uma **credencial Guardian em texto claro na variável de ambiente do
processo**, legível via `/proc/<pid>/environ` por processos do mesmo usuário. Não reproduzo o
valor. Recomendo rotacionar e mover para arquivo `600` ou para o mecanismo de segredo do systemd,
em gate próprio.
