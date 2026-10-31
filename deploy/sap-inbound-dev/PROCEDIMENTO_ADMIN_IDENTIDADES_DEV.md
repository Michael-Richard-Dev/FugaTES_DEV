# Procedimento único — provisionamento das identidades PostgreSQL DEV (R162)

Para um administrador autorizado, executado **inteiramente no terminal da VM ou numa sessão SSH
do próprio operador**. Nenhuma senha trafega por chat, log, pacote ou arquivo do repositório.

Alvo: `vm-fugatesbio` · cluster `18/fugatesacmdev` · `fugates_dev` · porta **5433**.
Nada é tocado em 5432, 5434, HML ou produção.

---

## O que será criado

| Identidade | Finalidade | Escrita? |
|---|---|---|
| `fugates_dev_read_app` | API privada GET-only **18762** | **não** — nenhum INSERT/UPDATE/DELETE |
| `fugates_sap_inbound_app` | receptor SAP (ainda não ativado) | somente nas 3 tabelas do contrato |

**`fugates_acm_hml_app` não é alterada.** Ela continua servindo o readback 18761 e o serviço de
comandos 18763, que dependem das escritas que possui.

---

## Pré-requisitos

1. Acesso `sudo` na VM (a senha é digitada apenas no terminal do administrador).
2. Backup físico verificável do `fugates_dev` **antes** de qualquer DDL.
3. Os dois pares de arquivos já versionados:
   - `docs/Incrementos/UnifiedDev/005_DEV_READ_ONLY_ROLE.sql` + `005_ROLLBACK_…`
   - `docs/Incrementos/UnifiedDev/004_SAP_INBOUND_WRITER_ROLE_DEV.sql` + `004_ROLLBACK_…`

---

## Passo 1 — Backup

```bash
sudo -u postgres pg_dump -p 5433 -Fc -d fugates_dev \
  -f /var/lib/postgresql/backup_fugates_dev_$(date -u +%Y%m%d_%H%M%SZ).dump
ls -l /var/lib/postgresql/backup_fugates_dev_*.dump
sha256sum /var/lib/postgresql/backup_fugates_dev_*.dump
```

Registrar caminho, tamanho e SHA-256. **Sem backup confirmado, não prosseguir.**

## Passo 2 — Identidade GET-only da API 18762

```bash
umask 077
mkdir -p ~/.fugates-dev-read
SENHA="$(openssl rand -base64 36 | tr -d '\n/:@' | cut -c1-40)"
printf '127.0.0.1:5433:fugates_dev:fugates_dev_read_app:%s\n' "$SENHA" > ~/.fugates-dev-read/pgpass
chmod 600 ~/.fugates-dev-read/pgpass

sudo -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 \
     -v senha="$SENHA" -f 005_DEV_READ_ONLY_ROLE.sql
unset SENHA
```

A senha é gerada na VM, gravada no pgpass com modo `600` e **nunca exibida**. O script tem guarda
de destino e postcheck que falha se a role receber qualquer escrita ou se a role compartilhada for
afetada.

Verificação (sem senha na saída):

```bash
PGPASSFILE=~/.fugates-dev-read/pgpass \
  psql "host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_dev_read_app" \
  -tAc "select 'leitura=OK role='||current_user||' readonly='||current_setting('default_transaction_read_only')"
```

## Passo 3 — Identidade de escrita do receptor SAP

```bash
umask 077
mkdir -p ~/.fugates-sap-inbound
SENHA="$(openssl rand -base64 36 | tr -d '\n/:@' | cut -c1-40)"
printf '127.0.0.1:5433:fugates_dev:fugates_sap_inbound_app:%s\n' "$SENHA" > ~/.fugates-sap-inbound/pgpass
chmod 600 ~/.fugates-sap-inbound/pgpass

sudo -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 \
     -v senha="$SENHA" -f 004_SAP_INBOUND_WRITER_ROLE_DEV.sql
unset SENHA
```

O postcheck do 004 recusa qualquer privilégio **além** do previsto (DELETE, TRUNCATE, UPDATE
irrestrito, privilégios de worker) e exige `UPDATE` restrito a `ultima_versao`.

Alternativa equivalente, a partir do Windows:
`deploy/sap-inbound-dev/Criar-RoleEscritaSap.ps1 -Backup <caminho>` — faz os mesmos passos numa
única sessão SSH interativa, pedindo a senha do sudo no terminal do operador.

## Passo 4 — Repontar a API 18762 para a identidade GET-only

Só depois do Passo 2 confirmado. A API lê a conexão do seu próprio ambiente; trocar o
`R165AL_APPROVED_PASSFILE` para `~/.fugates-dev-read/pgpass` **não basta**: a connection string
também declara o `Username`. Reiniciar o processo 18762 com a nova identidade, exatamente no
mesmo padrão do R161 (diretório novo preservado, processo antigo encerrado só após confirmação,
rollback = voltar ao anterior).

Requisito de código já atendido: `NpgsqlDataSourceFactory.EhRoleGuardada` reconhece
`fugates_dev_read_app`, de modo que a nova identidade passa pelas **mesmas** guardas físicas
(porta 5433, `fugates_dev`, schema `fugates`, host loopback, `Passfile` absoluto, verificação de
identidade por conexão). Sem essa alteração a API seria recusada com
`R165R_ACM_CONNECTION_METADATA_REQUIRED`.

## Passo 5 — Conferência final

```bash
sudo -u postgres psql -p 5433 -d fugates_dev -tAc "
select r.rolname, r.rolcanlogin, r.rolsuper,
       has_table_privilege(r.rolname,'fugates.integracao','INSERT') as ins_integracao,
       has_column_privilege(r.rolname,'fugates.integracao','estado','UPDATE') as upd_estado
  from pg_roles r
 where r.rolname in ('fugates_acm_hml_app','fugates_sap_inbound_app','fugates_dev_read_app')
 order by 1"
```

Esperado:

| role | ins_integracao | upd_estado |
|---|---|---|
| `fugates_acm_hml_app` | **t** (inalterada) | **t** (inalterada) |
| `fugates_dev_read_app` | **f** | **f** |
| `fugates_sap_inbound_app` | **t** | **f** |

## Rollback

Ordem inversa, cada um com guarda e postcheck próprios:

```bash
sudo -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -f 004_ROLLBACK_SAP_INBOUND_WRITER_ROLE_DEV.sql
# repontar a API 18762 para a identidade anterior ANTES de executar o 005_ROLLBACK
sudo -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -f 005_ROLLBACK_DEV_READ_ONLY_ROLE.sql
```

Ambos confirmam, no postcheck, que `fugates_acm_hml_app` permaneceu intacta.

---

## Achado de segurança a tratar em separado

O serviço `fugates-acm-manual-retry` (18763) expõe uma credencial Guardian **em texto claro na
variável de ambiente do processo**, legível por qualquer processo do mesmo usuário via
`/proc/<pid>/environ`. Não reproduzo o valor aqui. Recomendo **rotacionar essa credencial** e
movê-la para arquivo com modo `600` ou para o mecanismo de segredo do systemd
(`LoadCredential=`/`EnvironmentFile=` protegido), em gate próprio. Isso é independente das
identidades PostgreSQL deste procedimento.
