# Evidências R57 — cadastro do contrato 0000001160 no Guardian HML

Registro das verificações **efetivamente executadas** em 29/10/2026, para lastrear as referências
exigidas por `AcmR57NormalPreparation.Bloqueio()`. Nenhum atestado é presumido: cada item abaixo
corresponde a um comando executado e à sua saída.

---

## R169-GUARDIAN-IDENTITY-20261029

Identidade e acesso ao Guardian, distinguindo os dois canais.

**SOAP (canal do cadastro de contrato)** — `http://192.168.17.91:80/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx`

- Alcançável a partir da VM `vm-fugatesbio`: `HTTP 200`.
- WSDL obtido (1 517 694 bytes); operação `CadastraContrato` presente, com `SOAPAction`
  `http://toledobrasil.com.br/WS_Guardian/CadastraContrato`.
- Operação de leitura `ConsultaContrato` exercida com sucesso (ver abaixo).
- Autenticação: **nenhuma credencial é exigida neste canal** — o serviço é interno, em rede privada.
  O produto só aprova HTTP para este host e porta, em ambiente HML
  (`GuardianHttpOptions.ValidateContratoSoapEndpoint`).
- Não há operação de consulta de emissor ou item no WSDL: o código do emissor só se verifica no
  próprio cadastro, que devolve erro de negócio se não existir.

**REST (canal do pré-cadastro, etapa distinta)** — `https://guardian.cloudprix.com.br`

- `POST /api/v2/CreateToken` respondeu `HTTP 200`, com `succeeded`, `tokenType`, `token`.
- Credenciais vindas de `/etc/fugates/acm-r165y/guardian-rest-hml.env`, carregado pelo systemd.
  Nenhum valor foi lido, copiado ou registrado.
- Resolve para `4.228.156.98` (`app-waf-cloudprix-prod`).

Este canal **não** participa do cadastro de contrato.

---

## R169-POSTGRESQL-IDENTITY-20261029

Identidade física do banco e privilégios do motor.

- Cluster `18/fugatesacmdev`, base `fugates_dev`, porta **5433**, host `127.0.0.1`.
- Role do motor: `fugates_acm_hml_app`, autenticando por `Passfile`
  `/home/michael/.fugates-acm-r165p/pgpass`.
- Privilégios confirmados por catálogo: `INSERT` em `fugates.integracao`, `INSERT` em
  `fugates.contrato_sap_toledo_versao`, `UPDATE` em `fugates.integracao.estado`.
- Identidade separada do receptor SAP (`fugates_sap_inbound_app`), que deliberadamente **não** tem
  `UPDATE` em `integracao.estado` — mudar estado é papel do motor.
- A role de leitura da API 18762 (`fugates_dev_read_app`) permanece sem qualquer escrita:
  **0** privilégios de `INSERT`/`UPDATE`/`DELETE`/`TRUNCATE` em `fugates` e `masterdata`.

---

## R169-JOURNAL-20261029

Journal durável e comprovação de que as recusas ocorreram **antes** de chamada externa.

Contrato `0000001160`, integração `a6491514-5954-4752-a5e1-7a33be6ea09d`, recebida em
29/10 10:53:14 por `SAP → TOLEDO`, operação `SAP_CONTRATO_TOLEDO`.

Duas tentativas registradas, ambas `desfecho=FALHA`:

| Tentativa | Criada | Resultado recebido | Duração | `envio_iniciado_em` |
|---|---|---|---|---|
| `2a8cae32-461d-4731-a2b6-f7839b5ff762` | 11:04:20.764 | 11:04:20.893 | **129 ms** | **nulo** |
| (segunda) | 11:54:24.883 | 11:54:25.015 | **133 ms** | **nulo** |

Auditoria de cada tentativa: `claim → tentativa_criada → envio_iniciado → resultado →
retry_agendado`, com `diagnostico` vazio em todos os eventos.

**Prova de que não houve POST externo**, por três vias independentes:

1. **Tempo.** O ciclo completo levou 129 ms e 133 ms. Só a obtenção de token no Guardian REST
   levou 161 ms, e o próprio host responde em 93 ms. Não cabe uma chamada externa no intervalo.
2. **Código.** `Bloqueio()` é avaliado antes do gateway e retornava
   `R165U_EXECUTION_DISABLED`, por `AuthorizationReference` ausente. `GuardianContratoSoapClient`
   nunca foi alcançado.
3. **Guardian.** `ConsultaContrato` para o código `1160` devolveu
   `Erro=309851 — "O código do contrato informado não existe!"`, em 29/10, nas formas `1160` e
   `0000001160`. Se algum POST tivesse transmitido, haveria registro.

Observação exigida pelo gate: o evento `envio_iniciado` **não** comprova transmissão — ele é
gravado antes da chamada, e a coluna `envio_iniciado_em` permanece nula nas duas tentativas.

---

## R169-MAPEAMENTO-20261029

Mapeamento SAP → Guardian fornecido pelo operador em 29/10, para este contrato:

| SAP | Guardian |
|---|---|
| `IdParceiro` `0020000051` | `CodigoEmissor` `20000051` |
| `Material` `000000000007000002` | `CodigoItem` `7000000` |

Valores de negócio confirmados pelo operador: `Estado` `308102` (ativo, status `T`),
`TipoLancamento` `309838` (débito — venda), `ValorProximidadeFimContrato` `0`.

**Ressalva registrada:** no contrato 1031 o parceiro `0020000184` corresponde ao emissor Guardian
`10000`, o que indica que o código do emissor **não** é derivado do número SAP. O valor `20000051`
foi fornecido pelo operador e não pôde ser verificado por leitura — o WSDL não expõe consulta de
emissor. Se o código não existir, o `CadastraContrato` devolverá erro de negócio e nada será criado.

As formas usadas no mapeamento são as **gravadas no banco**, não as do OData do SAP: o transporte
preenche o material com zeros à esquerda (`7000002` → `000000000007000002`) e **perde o `Item`**
(SAP envia `10`; o registro durável não traz a chave).

---

## R169-PROXIMIDADE-20261029

`ValorProximidadeFimContrato = 0`, confirmado pelo operador em 29/10 para contrato de venda, e
coerente com o comentário do payload de referência do próprio Guardian ("Sempre 0").

---

## Escopo da autorização

`R169-OPERADOR-1160` — autorização explícita do operador, registrada no gate R169:
um único cadastro real do contrato `0000001160` no Guardian HML por SOAP `CadastraContrato`,
condicionado à ausência de cadastro prévio. Não constitui aprovação corporativa de terceiros.

---

## R170-BINARIO-20261029 — por que as três tentativas falharam

As tentativas de 29/10 às 11:04, 11:54 e 13:27 falharam todas pelo **mesmo** motivo, e não por
falta de autorização como eu havia suposto ao escrever a seção `R169-JOURNAL-20261029`.

O motivo estava gravado em `fugates.projecao_resultado_fluxo_mvp`, não no journal de tentativas:

| campo | valor |
|---|---|
| `etapa_funcional` | `R165U_PRE_SEND_BLOCKED` |
| `erro_codigo` | `R165U_BUSINESS_SCOPE_NOT_APPROVED` |
| `atualizado_em` | 2026-10-29 13:27:29.906331+00 |

`R165U_BUSINESS_SCOPE_NOT_APPROVED` vem de `AcmR57NormalRuntimeGate.Bloqueio()`, na checagem de
`AcmInboundWindowOptions.EscopoContratoAprovado`. O contrato é de venda (`AC3S/S/40`), e o release
em execução — `R165Y_PARTNER_20261025_R3`, compilado em 25/10 — antecede a extensão R170, que
passou a aceitar venda.

Verificado nos próprios binários, contando literais UTF-16 (`tr -d '\000' | grep -c`), método
conferido nos dois lados antes de ser usado como critério:

| string | release 25/10 em execução | binário publicado em 29/10 |
|---|---|---|
| `AC3P` | 1 | 1 |
| `309837` (compra, crédito) | 1 | 1 |
| `AC3S` | **0** | 1 |
| `309838` (venda, débito) | **0** | 1 |

Consequências para o registro:

- A recusa é **anterior** a qualquer chamada externa, o que reforça — por um quarto caminho — que
  nenhum POST foi transmitido. O Guardian segue devolvendo `Erro=309851` para `1160`.
- A ressalva da seção `R169-JOURNAL-20261029` continua válida quanto ao fato (não houve POST), mas
  a causa que eu atribuí ali — `AuthorizationReference` ausente — está **errada**. As chaves
  `AcmR57__Normal__Enabled`, `ExecutionAuthorized` e `GuardianEnvironment=HML` já estavam no
  `motor.env` desde 12:11 de 29/10. O bloqueio era de escopo de negócio, não de governança.
- A cadeia SAP → FugaTES → motor estava inteira: o SAP entregou, o receptor gravou, o predicado SQL
  da janela admitiu o contrato e o motor o reivindicou (`claim`, `fencing_token=3`). Só a última
  porta recusou.

Correção aplicada: release `R170_VENDA_20261029_R1`, publicado do código atual, instalado ao lado do
anterior (nada sobrescrito) por `deploy/sap-inbound-dev/promover-motor-r170.sh`, com reversão
apontando a unit de volta para `R165Y_PARTNER_20261025_R3`.

Varredura de testes do código promovido: 13 falhas, todas pré-existentes e de ambiente — oito
exigem `evidence/R165Y_AUTO_1144/PILOT_1144_RESULT.json`, que nunca esteve no repositório; as outras
checam perfil empacotado, entrypoint compilado e as duas regressões já conhecidas. Nenhuma toca
escopo de contrato. O projeto `R158.SapInboundReadiness`, que cobre compra e venda, passou íntegro.

---

## R169-EXECUCAO-20261029 — cadastro realizado

POST SOAP `CadastraContrato` executado pelo motor FugaTES em 29/10/2026 14:25:49 UTC, release
`R170_VENDA_20261029_R1`, endpoint interno `192.168.17.91:80`. Um único POST.

`ConsultaContrato` para `0000001160`, após a execução:

| campo | valor |
|---|---|
| `Erro` | `0` — "Contrato consultado com sucesso!" |
| `Sequencial` | **588** |
| `Descricao` | `ACM contrato venda a terceiros` |
| `Estado` | `308102` |
| `TipoLancamento` | `309838` |

`Estado` e `TipoLancamento` conferem com o mapeamento de venda confirmado pelo operador. Sequencial
588 é o imediatamente seguinte ao 587 do teste Postman de 28/10, coerente com exatamente **um**
cadastro novo. Quatro tentativas no journal; somente a de 14:25:49 enviou.

O pipeline parou em seguida na etapa REST com `R165U_CALLBACK_APPROVAL_REQUIRED`, porque
`AcmR57__Normal__CallbackApproved=false`. O pré-cadastro REST é etapa distinta e **não** estava
autorizado neste gate. A parada é o comportamento correto, não uma falha.

### Correções a afirmações anteriores deste documento

1. **`envio_iniciado_em` nulo NÃO comprova ausência de transmissão.** A coluna permaneceu nula
   também na tentativa que efetivamente cadastrou o contrato. A ressalva registrada em
   `R169-JOURNAL-20261029` sobre o evento `envio_iniciado` estava certa; a inferência que eu
   acrescentei a partir da coluna estava errada. Para as três primeiras tentativas a ausência de
   POST continua comprovada, mas pelo código de bloqueio pré-envio
   (`R165U_BUSINESS_SCOPE_NOT_APPROVED`, etapa `R165U_PRE_SEND_BLOCKED`), não pela coluna.

2. **`desfecho=FALHA` na tentativa não significa que o cadastro falhou.** A tentativa de 14:25:49
   está marcada `FALHA` porque o pipeline terminou bloqueado na etapa REST. O SOAP foi bem-sucedido.

3. **A verificação de duplicidade consultava a forma errada do código.** O script usava `1160`,
   enquanto o motor envia `contrato.NumeroContrato` = `0000001160`. Um cadastro existente na forma
   longa responde `309851` na forma curta, de modo que a trava não protegia. Neste caso não houve
   duplicidade — o contrato realmente não existia antes —, mas por sorte, não por controle.
   A afirmação de `R169-JOURNAL-20261029` de que a ausência foi verificada "nas formas `1160` e
   `0000001160`" descreve uma consulta manual, não o que o script fazia. Corrigido: a reconciliação
   agora consulta as duas formas e exige ausência em ambas.

### Confirmação visual no Guardian (CloudPrix), 29/10/2026

Tela de seleção de contrato do CloudPrix, conferida pelo operador:

| Código | Descrição | Emissor | Item | Saldo atual |
|---|---|---|---|---|
| `0000001160` | ACM contrato venda a terceiros | `20000051` | `7000000` | 100.000 kg |

Isto encerra a ressalva registrada em `R169-MAPEAMENTO-20261029`. O código de emissor `20000051` e
o item `7000000` haviam sido fornecidos pelo operador e **não podiam ser verificados por leitura** —
o WSDL não expõe consulta de emissor nem de item. A tela confirma que ambos existem no Guardian e
que o contrato foi criado vinculado a eles. O saldo de 100.000 kg corresponde a
`QuantidadeContrato = 100000.000` do snapshot SAP, ou seja, a quantidade foi transportada sem
substituição por `ValorUnitario`.

---

## R170-CONSULTAPESSOA-20261030 — por que não há como verificar emissor por leitura

Tentativa de validar um código de emissor antes de mapeá-lo, usando `ConsultaPessoa` do WSDL
(único candidato: aceita apenas `CodigoPessoa`, `CPF` ou `DocEstrangeiro`; não busca por nome nem
lista). O resultado invalida o método:

| código consultado | resultado | contratos reais que o usam |
|---|---|---|
| `20000051` | `Erro=311941` não localizado | 1160 (seq 588), 1162 (seq 589), 1163, 1164 |
| `20000184` | `Erro=311941` não localizado | 1026 (seq 590) |
| `20000610` | `Erro=311941` não localizado | — |

Emissores que comprovadamente registram contrato devolvem "não localizado". Logo `ConsultaPessoa`
consulta outro cadastro, não o de emissores — **não serve** para confirmar nem para descartar um
código. O script `conferir-emissor-guardian.sh` foi removido: ele produzia falso negativo, e uma
ferramenta que afirma ausência com segurança falsa é pior que não ter ferramenta.

**Consequência prática:** o código de emissor só se confirma na tela do CloudPrix ou no próprio
`CadastraContrato`. Se o código estiver errado, o Guardian devolve erro de negócio e **nada é
criado** — a falha é limitada e visível (`R165U_SOAP_BUSINESS_REJECTED`, estado
`FALHA_NAO_REPROCESSAVEL`), sem registro parcial.

Isto reforça a ressalva de `R169-MAPEAMENTO-20261029`: o código do emissor não é derivável do
número SAP e não é verificável por leitura. O padrão "parceiro SAP sem os dois zeros iniciais"
(`0020000051` → `20000051`) vale para os casos observados, mas é observação, não regra: no contrato
1031 o parceiro `0020000184` corresponde ao emissor `10000`.
