# Devolutiva — HERA V4

`GATE=FUGATES-SAP-DEV-FINAL-ACTIVATION-PREPARATION` · `EXECUTOR=CLAUDE_CODE` · `RESPONDE_PARA=HERA_V4`
Encerrado em 28/10/2026.

```
DEV110_VERSION=1.2.5+toledo.receipts.monitor.20261028.b30de487
READ_API_ROLE_SWITCH=PASS
READ_API_ROLE=fugates_dev_read_app
READ_API_GET_ONLY=PASS
DEV110_READBACK=PASS
SAP_WRITER_AUTH=PASS
SAP_RECEIVER_CONFIG=PASS
SAP_RECEIVER_INSTALLED=NO
SAP_RECEIVER_ACTIVE=NO
SAP_DEV_INGRESS=PENDING
R57_WINDOW=CLOSED
REMAINING_HUMAN_DECISIONS=5
EXTERNAL_POSTS=0
NEXT_HANDOFF=HERA_V4
```

---

## Missão A — API 18762 promovida e validada

A identidade GET-only está em produção no DEV. Estado verificado no encerramento:

| Verificação | Resultado |
|---|---|
| `GET /dev/read/identity` | **200** · `18/fugatesacmdev` · `5433` · `fugates_dev` · **`fugates_dev_read_app`** · `readOnly:true` |
| `contracts` · `monitor` · `masterdata` · `toledo` | **200** em todos |
| Privilégios de escrita da role leitora em `fugates` + `masterdata` | **0** |
| Configuração da role | `default_transaction_read_only=on`, `search_path=fugates` |
| `CREATE` no banco | `false` |
| `integracao` | legível **por coluna**, nunca por tabela inteira |
| 18763 | `pid=651989`, o mesmo da linha de base — não reiniciado |
| Listeners | `8080`, `18761`, `18762`, `18763`, `5432`, `5433`, `5434`, `80`, `22` — todos no ar |

Backup e ponto de rollback gravados a cada troca. `DEV110_READBACK=PASS` por verificação humana: o
operador abriu o executável instalado e obteve `PostgreSQL 5433: CONECTADO`, lendo pela API privada
com a identidade nova.

### O abort da tentativa anterior

Causa raiz: o guard de identidade comparava `current_user` com a role ACM compartilhada, **fixa no
código**. Ao subir como `fugates_dev_read_app`, a própria identidade configurada era recusada
(`UNIFIED_DEV_DATABASE_IDENTITY_REJECTED`). O `Aborted (core dumped)` era apenas como o .NET
encerra em `SIGABRT` — sintoma, não causa. Não houve falha de runtime, permissão de arquivo,
dependência .NET nem conflito de listener.

Eram **quatro** pontos de recusa, não um, e três deles só apareceriam depois da partida:

1. `DevUnifiedReadStore.Guard`
2. `DevDatabaseIdentity.Validate()`
3. `MonitorPostgreSqlReadRepository` e `ContratosAcmPostgreSqlReadRepository`
4. `RecebimentosToledoPostgreSqlReadRepository.Protect()` — surgiu depois, em código do Codex

Correção sem afrouxar controle: `SqlIdentidadeAcm` ficou **intacta** para os caminhos estritos, e
foi acrescentada `SqlIdentidadeRole`, idêntica em todas as condições físicas, com a role entrando
como **parâmetro** `@role`, nunca interpolada. A lista branca tem exatamente duas identidades de
leitura; `fugates_sap_inbound_app` está fora dela de propósito. Uma role não autorizada é recusada
**antes de abrir conexão**. O parâmetro é opcional e nulo preserva o comportamento histórico, de
modo que nenhum chamador existente mudou.

### Deltas de banco aplicados

| SQL | Efeito | Estado |
|---|---|---|
| `006_DEV_ROLES_CONNECT_GRANT` | `CONNECT ON DATABASE` para as duas roles novas | aplicado |
| `007_DEV_READ_ROLE_TOLEDO_SELECT` | SELECT nas 4 tabelas Toledo + 2 colunas de `integracao` | aplicado |

Ambos transacionais, com guardas físicas, postcheck que aborta em qualquer excesso e rollback
escrito. Nenhum concedeu escrita, nada foi dado a `PUBLIC`, a identidade de escrita do receptor não
ganhou leitura Toledo e `fugates_acm_hml_app` permanece inalterada.

O `006` corrigiu uma lacuna que teria parado o recebimento SAP na ativação: nem o `004` nem o `005`
concediam `CONNECT`, e nesta base o `CONNECT` do `PUBLIC` está revogado — as duas roles autenticavam
e eram recusadas na porta.

---

## Missão B — Receptor SAP DEV pronto, não instalado

`FUGATES_SAP_INBOUND_RECEIVER_DEV.zip` · SHA-256 `1FEB21CC…` · 24 arquivos · `installed=false` ·
**zero credenciais**.

| Item | Estado |
|---|---|
| Autenticação de `fugates_sap_inbound_app` | **PASS** — `fugates_sap_inbound_app;fugates_dev;5433;18/fugatesacmdev` |
| Porta loopback | **18764**, escolhida por preflight, livre e fora de todo serviço existente |
| Credencial Basic | fora do appsettings e do repositório; entra pelo ambiente do processo |
| Credencial Guardian | **dispensada** na partida, sem reutilizar segredo exposto |
| Workers Guardian | desabilitados |
| Janela R57 | fechada |
| HML 8080 | intocado |

**Credencial Basic.** Gerada na VM por `Preparar-CredencialBasic.ps1`, gravada em arquivo modo 600 e
devolvida apenas como impressão digital SHA-256. Não passa pelo Windows, não entra no repositório e
não aparece em log nem em linha de comando: o instalador a injeta por `set -a; . basic.env; set +a`.
A entrega ao emissor SAP/CPI é passo humano, pelo canal aprovado.

**Guardian na partida.** O guard SAP exige `ExternalMode=Guardian`, e isso obrigava a provisionar um
segredo que este receptor nunca usaria. O diferimento exige opt-in explícito **e**, cumulativamente,
nenhuma execução Guardian autorizada; qualquer uma ligada recoloca a exigência no boot. O controle
não se moveu: sem credencial, nenhuma chamada Guardian ocorre.

**Duas barreiras de admissão, ambas de pé.** Como entregue, a composição recusa na primeira
(`SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED`); fornecida a credencial, recusa na segunda
(`SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED`). Há teste fixando que uma não substituiu a
outra. Comprovado também em execução real: o host morre com o código do guard e **não abre
listener**.

**Isolamento do trabalho Toledo do Codex.** Os arquivos dele não podem ser retirados do assembly sem
quebrar o build em curso. Em vez disso, está provado que o receptor **não os compõe**: `Program.cs`
tem zero referências a Toledo, e o único caminho até `ToledoCallbackComposition` passa por
`Callback:Enabled`, que a configuração entregue declara `false`. Três testes asseguram isso sobre o
arquivo real entregue.

**Instalação e rollback.** `Instalar-ReceptorDev.ps1` recusa portas de serviços existentes, confere
o SHA, exige configuração sem pendências, valida a credencial Basic e a autenticação da role de
escrita antes de instalar, faz backup da instalação anterior e, na subida, exige `/health` 200 **e**
404 nas rotas de callback Guardian e Toledo. Em qualquer falha derruba o novo, marca `_FALHOU`,
preserva o anterior e devolve `BLOCKED`. Ensaiado em `-Conferir`.

---

## Missão C — As cinco definições que dependem de decisão

Reduzidas de sete: porta, passfile e credencial Guardian foram fechados tecnicamente.

| # | Definição | Quem confirma |
|---|---|---|
| 1 | Destino HTTPS DEV: host, porta, nome DNS e certificado | Infraestrutura + Hera |
| 2 | Identidade efetiva do emissor SAP/CPI para o DEV (IP/tenant) | SAP/CPI |
| 3 | Usuário Basic acordado e entrega do segredo pelo canal aprovado | Hera + SAP/CPI |
| 4 | Contrato novo de homologação: centro, parceiro e material | SAP/negócio |
| 5 | Janela R57: `WindowId`, `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` (≤ 1 dia) | Hera |

Os dois únicos marcadores restantes no pacote são os do item 5. Nenhuma allowlist foi ampliada e
nenhuma referência de autorização foi inventada. A proposta de ingress nginx segue pronta e **não
publicada**: o ingress HML atual não serve ao DEV porque envia `X-Forwarded-Proto: http`, remove o
header `Authorization` e aponta para o serviço HML.

---

## Missão D — Teste final

Não executado, conforme o gate: depende de autorização específica para o ingress corporativo e para
a janela R57. A sequência de comprovação está definida — SAP/CPI → FugaTES → `COMMIT` 5433 → H4 →
API 18762 → DEV110 — com Guardian SOAP/REST desabilitado durante o primeiro ingresso e sem reenvio
de histórico.

---

## Regressão

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 149 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 283 | 0 |
| R165A.Tests | 43 | 0 |
| FugaTES.Testes | 1631 | 2 |

As duas falhas **não são deste gate** e apontam para o mesmo arquivo novo do Codex,
`src/FugaTES.Integracao.SAP/Http/DocumentoToledoSapGateway.cs`, por conter `HttpMethod.Post`:
`Seguranca_CodigoProdutivo_NaoContemRegressoesDoIncremento001` e
`SegurancaInc005_NaoContemTransporteRealCredenciaisOuDdlExecutado`. São guards de **segurança** que
proíbem transporte real nessa camada — recomenda-se avaliação antes de consolidar a frente Toledo.

As suítes `R165U.Automatic.Tests` e `R165R.Provider.Tests` continuam dependendo de ambiente
(`Frozen approved evidence not found.` e `Set R165R_PACKAGE_ROOT…`); o diretório
`evidence/R165Y_AUTO_1144/` é não rastreado pelo git e está ausente da árvore. Nenhuma evidência
congelada foi fabricada.

Três testes meus foram **atualizados, não enfraquecidos**, porque o comportamento mudou de
propósito: a configuração entregue passou a recusar primeiro pela credencial Basic e a porta deixou
de ser pendência. Em ambos os casos foram acrescentadas asserções que fixam a barreira seguinte, de
modo que a admissão R57 continua provada como obrigatória.

---

## Correções de leituras minhas, registradas

1. **Túnel SSH.** O DEV110 não conectava porque eu havia aberto um túnel à mão para o validador
   automático. O Desktop monta o próprio túnel e compara a linha de comando do processo dono da
   18761 com uma lista de argumentos exata; o meu diferia e ainda ocupava a porta. O guard agiu
   corretamente e **não enviou chave nenhuma**. Encerrado o túnel, conectou no primeiro clique.
2. **Entrada Toledo.** Atribuí a falha do validador automático a ela estar desabilitada no build
   instalado. Está habilitada: a fiação do Codex está feita. A captura cinza vinha do próprio
   harness, que constrói `FrmIntegracoes` sem passar `toledoRead` — lacuna do verificador.
3. **Alcance do 007.** Afirmei que resolveria o 503 do Toledo. Os GRANT eram necessários, mas não
   suficientes: faltava o quarto guard de identidade.

## Coordenação com o Codex

Nenhum arquivo dele foi modificado. Quando o quarto guard foi identificado, a decisão do operador
foi entregar o patch em vez de editar o arquivo — e o Codex aplicou a correção por conta própria,
usando exatamente o mesmo padrão (`SqlIdentidadeRole` com `@role`), e em seguida reimplantou o
18762 como `20261028_TOLEDO_READ_ROLE_R2_FE331BDD46C8` **preservando**
`FUGATES_DEV_READ_ROLE=fugates_dev_read_app`.

Resultado: a promoção GET-only sobreviveu à reimplantação dele e `/dev/read/toledo` passou a
responder **200 com dados**. As duas frentes convergiram sem conflito.

## Próximo passo

Fornecer as cinco definições da Missão C. Com elas, o receptor é instalado em um passo
(`Instalar-ReceptorDev.ps1`), a rota DEV é publicada a partir da proposta nginx, a janela R57 é
aberta pelo tempo da homologação e o SAP/CPI envia **um** contrato novo.
