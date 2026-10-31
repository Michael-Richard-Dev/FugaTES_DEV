# R166 — O abort da API 18762 e por que a promoção fica retida

Gate `RESOLVER_ABORT_NOVA_API_18762`. Nenhum DDL, GRANT, POST, ativação de receptor ou janela R57.
`fugates_acm_hml_app` preservada. 18761, 18763 e 8080 intocados.

---

## Etapa 1 — Rollback: saudável

O rollback automático do roteiro devolveu o serviço ao release validado.

| Verificação | Resultado |
|---|---|
| Listeners | `18761`, `18762`, `18763`, `8080` no ar |
| Release ativo do 18762 | `…/20261028_144029_TODOS_R2` |
| `GET /dev/read/identity` | **HTTP 200** · `18/fugatesacmdev` · `5433` · `fugates_dev` · `fugates_acm_hml_app` · `readOnly:true` |
| Contratos | **6** |
| 18763 | `pid=651989` — **o mesmo** da linha de base |

## Etapa 2 — Causa raiz

O `Aborted (core dumped)` é apenas como o .NET encerra em `SIGABRT` numa exceção não tratada: é
sintoma, não causa. As duas tentativas (`20261028_183810` e `20261028_190902`) registraram a mesma
coisa no `reader.log`:

```
Unhandled exception. System.InvalidOperationException: UNIFIED_DEV_DATABASE_IDENTITY_REJECTED
   at DevUnifiedReadStore.Guard(...)
   at DevUnifiedReadStore.IdentityAsync(...)
   at DevUnifiedReadApi.RunAsync()
```

**O guard de identidade comparava `current_user` com a role ACM compartilhada, fixa no código.**
Ao subir como `fugates_dev_read_app`, a própria identidade configurada era recusada. Não houve falha
de runtime, de permissão de arquivo, de dependência .NET nem conflito de listener — e não havia
necessidade de consultar coredump.

Eram **três** pontos de recusa, não um, e os dois últimos só apareceriam depois da partida, a cada
leitura:

1. `DevUnifiedReadStore.Guard` → predicado `SqlIdentidadeAcm`, com `current_user='fugates_acm_hml_app'`;
2. `DevDatabaseIdentity.Validate()` → `Role!="fugates_acm_hml_app"`;
3. `MonitorPostgreSqlReadRepository` e `ContratosAcmPostgreSqlReadRepository`, que o store constrói
   com `exigirIdentidadeAcm:true` e usam o mesmo predicado.

## Etapa 3 — Correção

`SqlIdentidadeAcm` é uma `const` **compartilhada** com o monitor e os contratos ACM. Alterá-la
afrouxaria o controle também nesses caminhos, que devem continuar restritos à role compartilhada.
Por isso ela ficou **intacta**, e foi acrescentada uma variante parametrizada:

- `SqlIdentidadeRole` — idêntica em **todas** as condições físicas (porta 5433, cluster, base,
  `transaction_read_only`, `inet_server_addr`, versão, schema); só a role sai do literal para `@role`,
  ligada como **parâmetro**, nunca interpolada.
- `RolesDeLeituraAutorizadas` = exatamente `fugates_acm_hml_app` e `fugates_dev_read_app`.
  **`fugates_sap_inbound_app` não está na lista**, de propósito: a identidade de escrita do receptor
  nunca serve leitura.
- `ComandoIdentidade(roleEsperada, …)` recusa qualquer outro valor com
  `ACM_DATABASE_IDENTITY_ROLE_NOT_AUTHORIZED`, **antes de abrir conexão**.
- `roleEsperada` é um terceiro parâmetro **opcional** nos dois repositórios e no store. Nulo = o
  comportamento histórico, fixo na role compartilhada. Todo chamador existente segue idêntico.
- `Validate()` sem argumento aceita as duas identidades de leitura — é o que o **cliente** (DEV110)
  usa, pois ele recebe a identidade pela API e não sabe qual o servidor configurou.
  `Validate(role)` exige exatamente uma: é o que o **servidor** usa, que sabe o que configurou.
  Nenhuma das outras condições físicas foi relaxada.

Nada foi afrouxado para a API subir, e nenhum teste foi enfraquecido para obter PASS.

## Build e testes

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| FugaTES.Testes | 1626 | 0 |
| R158.SapInboundReadiness.Tests | 71 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 262 | 1 |

A falha remanescente é `ToledoReceiptsFormTests` — arquivo **não rastreado**, criado hoje às 16:15:
a tela Toledo do Desktop que o Codex está construindo. Fora do meu caminho de alteração.

Testes novos acrescentados: a variante parametrizada fixa tudo menos a role; a lista branca aceita
as duas identidades de leitura e recusa a de escrita, `fugates_dev_app`, `fugates_dev_owner` e
`postgres`; o cliente aceita as duas e o servidor exige a exata; e a nova role continua sujeita a
porta, cluster, base e `readOnly`.

---

## Etapa 4 — A promoção fica retida, e por um motivo novo

**Promover agora quebraria o readback do DEV110.** Isto não havia sido identificado antes:

`PhysicalMonitorClient.ReadAsync` (`src/FugaTES.WinForms/Monitor/Physical/PhysicalMonitorClient.cs:150`)
chama `envelope.Identity.Validate()` **a cada leitura**. O `FugaTES.Persistencia.dll` **instalado**
em `C:\FugaTES_DEV110` foi verificado byte a byte:

```
fugates_acm_hml_app      presente
fugates_dev_read_app     AUSENTE
```

Ou seja: o executável instalado 1.2.4 só aceita a role compartilhada. Se a API passar a reportar
`fugates_dev_read_app`, o DEV110 recusa a identidade com `UNIFIED_DEV_DATABASE_IDENTITY_REJECTED` —
e o gate exige `DEV110_READBACK=PASS`. A promoção falharia a própria condição que ela precisa
satisfazer, e o item 10 mandaria reverter.

**Troca parcial de DLL não resolve.** Substituir apenas o `FugaTES.Persistencia.dll` levaria a
correção sem tocar o `Aplicacao.dll` em voo do Codex — mas o novo `Persistencia` referencia
`RecebimentosToledoSnapshot` e `RecebimentoToledoDocumento`, **ausentes** no `Aplicacao.dll`
instalado (verificado). O processo quebraria em carga de tipo.

**Republicar o Desktop inteiro é o único caminho — e é o conflito já documentado no R165:** o EXE
embarcaria o trabalho Toledo/Guardian inacabado do Codex, incluindo a tela cujo teste está vermelho.
O gate proíbe publicar componente incompleto.

### Dependência objetiva

| | |
|---|---|
| **Ação** | Republicar o Desktop DEV110 com a correção de identidade, depois promover a API 18762 |
| **Pré-requisito** | O Codex concluir o Toledo em `Aplicacao`/`Persistencia`/`WinForms`, com a suíte R165M verde |
| **Quem** | Codex conclui; Hera autoriza a republicação |
| **Destrava** | `READ_API_ROLE_SWITCH` e `DEV110_READBACK` juntos, na mesma janela |

A correção está pronta, compilada e coberta por testes. O pacote do opt-in permanece íntegro em
`.artifacts/R166_DEV_IDENTITIES/` (SHA `CD0EE627…`), agora em caminho durável. A API 18762 segue no
release validado, com a role compartilhada, atendendo HTTP 200 — isto é, **o estado atual é o estado
bom**, e nada precisa ser revertido.
