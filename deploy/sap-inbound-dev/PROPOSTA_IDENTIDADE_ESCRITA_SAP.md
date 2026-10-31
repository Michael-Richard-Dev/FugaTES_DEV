# Proposta — identidade de banco exclusiva do receptor SAP (DEV 5433)

`SAP_WRITER_IDENTITY_PLAN=READY`. **Nada foi executado.** Nenhum `CREATE ROLE`, `GRANT` ou DDL.
Este documento é uma proposta arquitetural para decisão da Hera.

## Problema

A role `fugates_acm_hml_app` é, por construção, **leitora**. A única concessão registrada
(`003_MONITOR_COLUMN_READ_ACL_DEV.sql`, 27/10, `columnSelectAdded: 12`) declara no cabeçalho
*"No writer grants"*, e sua guarda exige que a role **não** seja membro de `fugates_dev_owner`.

Ela também é a identidade usada pela **API privada de leitura 18762**, que abre conexões com
`default_transaction_read_only=on`. Conceder `INSERT`/`UPDATE` a essa role ampliaria o privilégio
de um componente cuja única função é ler — e a API de leitura passaria a ter, no catálogo,
capacidade de escrita que nunca deveria exercer.

**Recomendação: não conceder escrita à role leitora.** Criar uma identidade separada para o
receptor, com privilégio mínimo e escopo fechado.

## Identidade proposta

| Atributo | Valor proposto |
|---|---|
| Nome | `fugates_sap_inbound_app` |
| `LOGIN` | sim |
| `SUPERUSER` / `CREATEDB` / `CREATEROLE` / `BYPASSRLS` / `REPLICATION` | **não** |
| Membro de `fugates_dev_owner` | **não** |
| `CONNECTION LIMIT` | baixo (ex.: 4) — o receptor é uma instância única |
| Senha | via `Passfile` absoluto no host, como a role leitora; nunca na connection string |
| `search_path` | `fugates` |

## Privilégio mínimo — exatamente o que a transação exige

O receptor escreve em três tabelas, numa única transação
(`ContratoSapToledoVersaoStorePostgreSql.RegistrarAsync`):

| Objeto | Privilégio | Por quê |
|---|---|---|
| `fugates.contrato_sap_toledo_identidade` | `SELECT`, `INSERT`, `UPDATE(ultima_versao)` | upsert da identidade, `SELECT … FOR UPDATE`, avanço da última versão |
| `fugates.contrato_sap_toledo_versao` | `SELECT`, `INSERT` | detecção de reentrega por fingerprint e gravação da nova versão |
| `fugates.integracao` | `INSERT` | registro H4 via `RegistrarCandidatoNaTransacaoAsync` |
| sequências dependentes dessas tabelas | `USAGE` | somente se as chaves forem geradas por sequência |

**Fora do escopo, deliberadamente:** nenhum `DELETE`, nenhum `TRUNCATE`, nenhum privilégio em
`tentativa_integracao` ou `auditoria_integracao` (essas pertencem ao worker, que permanece
desabilitado), nenhum `GRANT` em schema, nenhum `DEFAULT PRIVILEGES`, nenhuma participação em
role de owner.

`UPDATE` restrito à coluna `ultima_versao` é suficiente e deve ser preferido ao `UPDATE` de tabela
inteira — é o único `UPDATE` que o código executa.

## Guardas que permanecem obrigatórias

A identidade nova **não** relaxa nenhuma barreira existente:

1. **Destino único.** `NpgsqlDataSourceFactory` só aceita porta 5433, `fugates_dev`, schema
   `fugates`, host loopback e `Passfile` absoluto. Hoje esse caminho reforçado é acionado pela
   comparação com `AcmApplicationRole`; ao adotar uma role nova, a fábrica precisa reconhecê-la
   igualmente — **alteração de código de uma linha, a ser feita no gate de ativação**, para que
   `fugates_sap_inbound_app` também passe por `CriarAcm` e pela verificação de identidade física
   a cada conexão. Sem isso, a role nova cairia no caminho genérico, sem essas guardas.
2. **Escopo ACM obrigatório.** `SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED` na partida e
   `SAP_INBOUND_SCOPE_NOT_AUTHORIZED` (422) por requisição.
3. **Janela R57 autorizada e com expiração**, duração ≤ 1 dia, parceiros e materiais em lista.
4. **Workers Guardian desabilitados** (`FUGATES_INTEGRATION_WORKER_ENABLED=false`).
5. **Separação de identidades**: a leitora `fugates_acm_hml_app` permanece somente leitura e
   continua servindo a API 18762; a escritora serve apenas o receptor.

## Ordem de execução proposta (gate futuro, com backup e postcheck)

1. Backup físico verificável do `fugates_dev` no 5433.
2. `CREATE ROLE fugates_sap_inbound_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS CONNECTION LIMIT 4;`
3. `GRANT USAGE ON SCHEMA fugates TO fugates_sap_inbound_app;`
4. Os `GRANT` da tabela acima — tabela a tabela, coluna a coluna no caso do `UPDATE`.
5. Postcheck por `has_table_privilege` / `has_column_privilege`, abortando a transação se
   qualquer privilégio exceder a lista.
6. Provisionar o `Passfile` no host, fora do repositório.
7. Ajuste de uma linha em `NpgsqlDataSourceFactory` para submeter a role nova às mesmas guardas.
8. Rollback: `REVOKE` dos privilégios concedidos e `DROP ROLE`; nada mais é tocado.

A verificação somente leitura do estado atual está em
[`VERIFICAR_PRIVILEGIOS_RECEPTOR.sql`](VERIFICAR_PRIVILEGIOS_RECEPTOR.sql) — ela não concede nada
e imprime `INBOUND_DATABASE_ACL=PASS|BLOCKED`.
