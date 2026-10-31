# R164 — Roteiro de execução administrativa assistida das identidades DEV

Gate `FUGATES-R164-DEV-IDENTITIES-EXECUTION`. Alvo `vm-fugatesbio` · `18/fugatesacmdev` ·
`fugates_dev` · porta **5433**.

Entrega: **um único roteiro** para execução supervisionada pelo operador, iniciado no PowerShell
do Windows, com o sudo digitado na própria sessão SSH. Nenhum passo privilegiado foi executado
por mim; nenhuma senha foi solicitada no chat, capturada, transportada ou registrada.

---

## Correção necessária antes do roteiro

Ao preparar a Fase 5 encontrei um erro no meu próprio procedimento do R163. Eu havia registrado
que repontar a API 18762 seria questão de configuração. **Não é:** em `DevUnifiedReadApi` o
`Username` da connection string vinha **fixo no código**:

```csharp
Username = NpgsqlDataSourceFactory.AcmApplicationRole
```

Repontar exige rebuild. Alteração mínima aplicada, com opt-in restrito:

```csharp
public const string RoleEnvironmentVariable = "FUGATES_DEV_READ_ROLE";
public static string RoleDeLeitura()
{
    var escolhida = Environment.GetEnvironmentVariable(RoleEnvironmentVariable);
    if (string.IsNullOrWhiteSpace(escolhida)) return NpgsqlDataSourceFactory.AcmApplicationRole;
    if (escolhida != NpgsqlDataSourceFactory.DevReadOnlyApplicationRole)
        throw new InvalidOperationException("UNIFIED_DEV_READ_ROLE_NOT_AUTHORIZED");
    return escolhida;
}
```

Sem a variável nada muda. A **única** alternativa aceita é a identidade GET-only; qualquer outro
valor — inclusive `fugates_sap_inbound_app` — é recusado, de modo que a API de leitura jamais
possa apontar para uma identidade com escrita. Destino, schema e `default_transaction_read_only`
continuam fixos no código.

Pacote correspondente: `FUGATES_UNIFIED_DEV_READ_API_ROLE_OPTIN.zip`, SHA-256
`CD0EE627C5C5156DB0F7C78979B863CF103EC9B7AA0970DB75AEDEFB8C60480E`, 17 arquivos + manifesto,
`installed=false`.

Testes: 10 casos novos (sem opt-in mantém a role compartilhada; com opt-in usa a GET-only; cinco
identidades diferentes recusadas). Suíte `R158.SapInboundReadiness.Tests` **62/62**.

---

## O roteiro

`deploy/sap-inbound-dev/Executar-IdentidadesDev.ps1` — seis fases, cada uma com veredito
explícito, parada imediata em falha e rollback restrito ao que foi efetivamente alterado.

| Fase | O que faz | Exigência do gate |
|---|---|---|
| 0 | Confere `hostname`/usuário e, no banco, `current_database`, porta, `cluster_name` e recovery. Recusa qualquer divergência | 1 |
| 1 | Hash SHA-256 dos dois SQLs aprovados; exige backup e o cria se faltar (sudo nesta janela) | 2 |
| 2 | Aplica `005` e depois `004`; senha do sudo pedida na própria sessão | 3, 4, 5 |
| 3 | Postcheck de ACL das três roles contra a matriz esperada; confirma a compartilhada intacta | 7, 8 |
| 4 | Autentica como `fugates_dev_read_app` e lê de fato, **antes** de tocar na API | 9 |
| 5 | Promove a API 18762 com backup `tar.gz`, verificação `identity` HTTP 200 e **rollback automático** | 10 |
| 6 | Valida DEV110, seletor TODOS, seis contratos, JSON 1147/item 10 e o 18763 | 11 |

Senhas das roles são geradas **na VM** (`openssl rand`), gravadas em pgpass `600` e nunca
impressas (requisito 6). A matriz de ACL verificada na Fase 3:

| role | INSERT `integracao` | INSERT `contrato_sap_toledo_versao` | UPDATE `integracao.estado` |
|---|---|---|---|
| `fugates_acm_hml_app` | true | true | true — **inalterada** |
| `fugates_dev_read_app` | false | false | false |
| `fugates_sap_inbound_app` | true | true | false |

Qualquer linha fora disso aborta com `ACL_ISOLATION=BLOCKED`.

O 18763 é verificado por **listener presente + identidade compartilhada autenticando**; nenhum
endpoint de comando é chamado, nenhum reenvio é disparado, o serviço não é reiniciado.

### Modos de uso

```powershell
# conferência, somente leitura — não altera nada
.\Executar-IdentidadesDev.ps1 -Conferir

# cria as duas roles e para antes de tocar na API
.\Executar-IdentidadesDev.ps1 -ApenasFase2

# execução completa, incluindo a promoção da API
.\Executar-IdentidadesDev.ps1 -Pacote <...\FUGATES_UNIFIED_DEV_READ_API_ROLE_OPTIN.zip>
```

Reexecutável: fases já concluídas são detectadas (roles existentes, backup presente) e puladas.

### Três defeitos de transporte encontrados e corrigidos

O roteiro só é confiável porque essas armadilhas foram descobertas e eliminadas em ensaio real:

1. **Aspas duplas no comando remoto.** O Windows remonta a linha de comando nativa e parte o
   argumento no `"`, fazendo o `bash` receber tokens soltos (`michael: command not found`).
2. **Conversão de LF para CRLF** quando o PowerShell escreve na entrada padrão de um processo
   nativo: o `\r` quebrava `head -1` (`invalid trailing option`) e os terminadores de here-doc
   (`here-document delimited by end-of-file`).
3. **Argumento vazio** sobrando ao anexar uma lista de parâmetros vazia.

Solução adotada: todo script remoto viaja **em base64, numa linha única**, decodificado na VM — e
os passos com sudo são gravados em `/tmp` e executados com `-tt`, deixando a entrada padrão livre
para o prompt da senha.

### Ensaio realizado

Modo `-Conferir` executado de ponta a ponta, sem erros:

```
ALVO                       PASS   vm-fugatesbio;michael | fugates_dev;5433;18/fugatesacmdev;false
SQL_INTEGRIDADE            PASS   005=C7F265682EAF81C8  004=395012ACC1B64916
BACKUP                     CONFERENCIA  nenhum backup presente
ROLES                      CONFERENCIA  existentes: -
ACL_ISOLATION              PASS   fugates_acm_hml_app=true,true,true
EXISTING_ROLE_UNCHANGED    PASS
```

---

## Estado após o ensaio

Nada foi alterado: as três roles pré-existentes seguem as mesmas, nenhuma role nova foi criada,
os listeners 8080/18761/18762/18763 seguem no ar e a API 18762 continua no release
`20261028_144029_TODOS_R2`. O único resíduo é `/tmp/r164_exec.sh` na VM, gravado pelo próprio
roteiro para transportar os comandos de leitura — inofensivo e sobrescrito a cada execução.

## Veredito das etapas

As fases 2, 4, 5 e 6 **não foram executadas** porque dependem da autenticação administrativa
interativa, que é exatamente o que este gate determina: *"Não executar por conta própria passos
privilegiados sem a participação autorizada do operador."* O roteiro está pronto, ensaiado na
parte verificável e aguarda apenas a execução supervisionada.
