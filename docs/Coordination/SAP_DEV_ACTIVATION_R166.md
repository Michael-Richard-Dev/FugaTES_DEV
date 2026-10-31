# R166 — Ativação SAP DEV: promoção da API 18762 e prontidão do receptor

Gate `FUGATES-SAP-DEV-FINAL-ACTIVATION-PREPARATION`. `EXTERNAL_POSTS=0`. Nenhum DDL, GRANT, replay
histórico, ativação de worker Guardian ou abertura de janela R57. HML 8080 e produção intocados.

---

## Missão A — API 18762 promovida

### Pré-verificação do DEV110 1.2.5

| | |
|---|---|
| Versão | `1.2.5+toledo.receipts.monitor.20261028.b30de487…` (28/10 16:32:52) |
| `FugaTES.exe` | `76F70EA481B9F604FBA993BED79E65BCD77ADC41EB46D3F95362F4BF7E1278E1` |
| `FugaTES.Persistencia.dll` | `6BFE8D19463F0A160AAAE994ED5C814734C00012FF39751847283F1E7356D850` |

Os **três guards de identidade** estão presentes no binário instalado: `current_user=@role`,
`SqlIdentidadeRole`, `RolesDeLeituraAutorizadas`, `ComandoIdentidade`, `fugates_dev_read_app` e
`ACM_DATABASE_IDENTITY_ROLE_NOT_AUTHORIZED`. O Desktop instalado aceita a identidade GET-only.

Integridade do trabalho do Codex no momento do empacotamento: build 0 erros e suítes verdes
(`FugaTES.Testes` 1633/0, R158 111/0, R165M 277/0, R165A 43/0). Os tipos Toledo dele estão tanto no
`Aplicacao.dll` quanto no `Persistencia.dll` instalados, coerentes entre si.

### Pacote usado

`.artifacts/R166_DEV_IDENTITIES/FUGATES_UNIFIED_DEV_READ_API_ROLE_OPTIN_R2.zip`
SHA-256 `3C0EF94B42FE015ECDF1BA9BA4EE73A74115D0DA21E17E55DF3BFF22E44C3DE4` · 20 arquivos ·
zero credenciais. O pacote anterior (`CD0EE627…`) **não servia**: foi construído antes da correção e
não continha os guards parametrizados — era exatamente a causa do abort.

### Resultado

```
identity  HTTP=200
{"cluster":"18/fugatesacmdev","port":5433,"database":"fugates_dev",
 "role":"fugates_dev_read_app","readOnly":true}
contratos HTTP=200  ·  6
```

Backup criado: `/home/michael/.fugates-unified-dev-read/BACKUP_API_20261028_211320Z.tar.gz`.
Release ativo: `…/20261028_211320_ROLE_OPTIN`.

### GET-only comprovado no catálogo

| Verificação | Resultado |
|---|---|
| Privilégios de escrita (INSERT/UPDATE/DELETE/TRUNCATE) em `fugates` + `masterdata` | **0** |
| Tabelas legíveis | 6 |
| Configuração de role | `default_transaction_read_only=on`, `search_path=fugates` |
| `CREATE` no banco | `false` |

### Preservação

Todos os listeners no ar: `8080`, `18761`, `18762`, `18763`, `5432`, `5433`, `5434`, `80`, `22`.
**18763 no mesmo `pid=651989`** da linha de base — não reiniciado, nenhum comando disparado.

### DEV110_READBACK — pendente de um passo seu

A fase 6 lançou `FugaTES.exe --validate-ui` no mesmo segundo em que o release do 18762 era trocado.
O Desktop pegou a janela de reinício e ficou preso em "Consultando contratos…" com a grade vazia,
gravando `UI_FAILURE.txt` e nenhum `UI_RESULT.json`. Não é defeito do binário nem da API: a API
responde 200 com os seis contratos, comprovado logo depois, e o túnel local está ativo.

A revalidação precisa que a janela travada do DEV110 seja fechada — tentei encerrá-la e a ação foi
recusada pela verificação de segurança do ambiente, corretamente, por ser encerramento de processo.

---

## Missão B — Receptor SAP DEV

`.artifacts/R166_DEV_IDENTITIES/FUGATES_SAP_INBOUND_RECEIVER_DEV.zip`
SHA-256 `1FEB21CCB452EB7D5766C17E76FAB4B9DF37DF382A2D6068D0BB795340A73C2F` · 24 arquivos ·
`installed=false` · **zero credenciais**.

### Porta escolhida por preflight: 18764

Verificada livre na VM e recusada pelo instalador se coincidir com qualquer serviço existente
(8080, 18761, 18762, 18763, 5432, 5433, 5434). A pendência de porta foi fechada.

### Credencial Basic — preparada sem expor segredo

A credencial **saiu do appsettings**, inclusive como placeholder. Ela entra pelo **ambiente** do
processo, que o `IConfiguration` lê com separador duplo:
`SapInbound__BasicAuth__Username` e `SapInbound__BasicAuth__Password`.

`deploy/sap-inbound-dev/Preparar-CredencialBasic.ps1` gera o segredo **na VM** (`openssl rand`),
grava em `/home/michael/.fugates-sap-inbound-receiver/basic.env` modo 600 e devolve apenas a
**impressão digital** SHA-256 — nunca o valor. O segredo não passa pelo Windows, não entra no
repositório e não aparece em log nem em linha de comando: o instalador o injeta com
`set -a; . basic.env; set +a`. O instalador confere existência, modo 600 e presença das duas
variáveis, e recusa instalar sem elas.

O **usuário** Basic não é inventado: vem do acordo com o emissor SAP/CPI e é passado por `-Usuario`.

### Composição sem credencial Guardian

Mantida e coberta por testes: `Guardian:CredentialsDeferredUntilUse=true` dispensa a exigência na
partida **apenas** com nenhuma execução Guardian autorizada; o provider continua recusando fechado
no ponto de uso. A configuração entregue não carrega credencial Guardian alguma.

### Duas barreiras de admissão, ambas de pé

Como entregue, a composição recusa na **primeira**: `SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED`.
Fornecida a credencial, recusa na **segunda**: `SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED`.
Uma não substituiu a outra — há teste fixando exatamente isso.

### Ensaio do instalador

```
ALVO          PASS   vm-fugatesbio;michael | destino 18764 livre
                     preservados: EMUSO:8080,EMUSO:18761,EMUSO:18762,EMUSO:18763
PACOTE        PASS   1FEB21CCB452EB7D
CONFIGURACAO  BLOCKED  2 definicoes empresariais pendentes
```

Na subida, o instalador exige `/health` 200 **e** 404 nas rotas de callback Guardian e Toledo; em
qualquer falha derruba o novo, marca `_FALHOU`, preserva o anterior e devolve BLOCKED.

---

## Missão C — O que ainda depende de decisão humana

Reduzido a cinco itens; porta, passfile e credencial Guardian foram fechados tecnicamente.

| # | Definição | Quem |
|---|---|---|
| 1 | Destino HTTPS DEV: host, porta, nome DNS e certificado | Infraestrutura + Hera |
| 2 | Identidade efetiva do emissor SAP/CPI (IP/tenant) para o DEV | SAP/CPI |
| 3 | Usuário Basic acordado com o emissor, e entrega do segredo pelo canal aprovado | Hera + SAP/CPI |
| 4 | Contrato novo de homologação: centro, parceiro e material | SAP/negócio |
| 5 | Janela R57: `WindowId`, `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` (≤ 1 dia) | Hera |

Os dois marcadores que restam no pacote são exatamente os do item 5. Nenhuma allowlist foi ampliada
e nenhuma referência de autorização foi inventada. A proposta de ingress nginx continua pronta e
**não publicada**.

---

## Regressão

Build 0 erros.

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 111 | 0 |
| R165A.Tests | 43 | 0 |
| FugaTES.Testes | 1632 | 1 |
| FugaTES.WinForms.Monitor.R165M.Tests | 281 | 1 |
| R165U.Automatic.Tests | 607 | 8 |
| R165R.Provider.Tests | 113 | 2 |

Três testes meus foram atualizados — não enfraquecidos — porque o comportamento mudou de propósito:
a configuração entregue agora recusa primeiro pela credencial Basic, e a porta deixou de ser
pendência. Em ambos os casos acrescentei asserções que **fixam a barreira seguinte**, de modo que a
R57 continua provada como obrigatória.

As 12 falhas não são minhas:

- **R165U (8) e R165R (2)** — dependências de ambiente, com mensagem explícita:
  `Set R165R_PACKAGE_ROOT…` e `Frozen approved evidence not found.` O diretório
  `evidence/R165Y_AUTO_1144/` é não rastreado pelo git e está ausente. Não cabe fabricar evidência
  congelada.
- **FugaTES.Testes (1)** — `Seguranca_CodigoProdutivo_NaoContemRegressoesDoIncremento001` acusa
  `src/FugaTES.Integracao.SAP/Http/DocumentoToledoSapGateway.cs: HttpMethod.Post`. Arquivo **não
  rastreado**, criado hoje às 18:33 pelo Codex. É um guard de **segurança** que proíbe POST nessa
  camada — vale ele avaliar antes de consolidar.
- **R165M (1)** — `ToledoSapReturnUiTests`, arquivo não rastreado de 18:32, também do Codex.

---

## Consequência da promoção: `/dev/read/toledo` 503, e o delta 007

Com a API servindo como `fugates_dev_read_app`, todos os recursos centrais respondem **200**:
`identity`, `contracts`, `monitor`, `detail`, `protocol`, `masterdata`. **Só `toledo` falha**, com
`503 UNIFIED_DEV_READ_UNAVAILABLE`.

Causa: o `005` concedeu SELECT numa lista **fixa**, definida antes de existirem as tabelas de
recebimento Toledo. A consulta do monitor precisa de quatro tabelas fora dessa lista:

```
fugates.toledo_pesagem_recebida = false
fugates.toledo_pesagem_item     = false
fugates.tentativa_integracao    = false
fugates.auditoria_integracao    = false
```

Antes funcionava porque o release anterior do 18762 rodava com a role compartilhada, de leitura
ampla. É uma regressão real da troca de identidade — e some assim que o `007` for aplicado.

`007_DEV_READ_ROLE_TOLEDO_SELECT.sql` (`EDC75EC2D63BB0EA`) concede **apenas SELECT** nessas quatro
tabelas, mais as duas colunas de `integracao` que a consulta usa (`worker_proprietario`,
`lease_expira_em`), mantendo `integracao` restrita por coluna. O postcheck aborta se aparecer
qualquer escrita, se `integracao` virar legível por inteiro, se a role de escrita do receptor ganhar
leitura Toledo ou se a role compartilhada for afetada. Rollback escrito.

`Aplicar-SelectToledoDev.ps1` aplica com sudo interativo e, ao final, prova de ponta a ponta que
`identity`, `contracts` e `toledo` respondem 200 e que o isolamento das três identidades continua
de pé. Ensaio `-Conferir` executado com sucesso.

## DEV110: por que o validador automático não certifica

Não é a API — ela está provada. No build instalado 1.2.5 a entrada **"Recebimentos Guardian /
Toledo" está desabilitada**, porque `FrmIntegracoes` faz `toledoRecebimentos.Enabled = toledoRead is
not null` e a composição ainda não injeta essa fábrica: é a fiação em curso do Codex, a mesma que
deixa `ToledoReceiptsFormTests` vermelho. O harness `--validate-ui` é um `try/catch` único que
engole a exceção e grava apenas `UNIFIED_UI_SMOKE_FAILED`, sem dizer onde parou.

A verificação do DEV110 fica com o operador, que tem a tela: conexão, seis contratos, filtro TODOS e
JSON do 1147/item 10. O túnel local `127.0.0.1:18761 → 18762` está ativo.

---

## Fecho: 007 aplicado, conexão comprovada na tela, e o quarto guard

### 007 aplicado pelo operador — PASS

```
fugates.auditoria_integracao=true | fugates.tentativa_integracao=true
fugates.toledo_pesagem_item=true  | fugates.toledo_pesagem_recebida=true
integracao_tabela_inteira=false   colunas_novas=true
escritas_da_role_leitora=0   writer_toledo=false   shared_insert=true
```

`integracao` continua restrita por coluna, a role leitora segue com **zero** escritas, a identidade
de escrita do receptor não ganhou leitura Toledo e a role compartilhada está intacta.

### DEV110 conectado — a promoção está provada na tela

O operador abriu o executável instalado e conectou: rodapé `PostgreSQL 5433: CONECTADO`, lendo pela
API privada já com a identidade GET-only. `DEV110_READBACK=PASS` por verificação humana.

Antes disso houve uma falha que **era minha**: eu havia aberto um túnel SSH à mão para o validador
automático, e o Desktop o recusou com *"Porta local 18761 ocupada por processo sem o binding SSH
aprovado. Nenhuma chave enviada."* O Desktop **monta o próprio túnel** e compara a linha de comando
do processo dono da 18761 com uma lista de argumentos exata (`LocalReadTunnel.BuildArguments`).
O meu tinha argumentos diferentes e ainda ocupava a porta. O guard agiu corretamente — não enviou
chave nenhuma. Encerrei o túnel manual e a conexão passou no primeiro clique.

### Correção de duas leituras minhas

1. Eu havia atribuído a falha do validador à entrada Toledo estar desabilitada no build instalado.
   A tela do operador mostra que ela está **habilitada**: a fiação do Codex está feita. A captura
   cinza vinha do próprio harness, que em `UnifiedDesktopVisualCheck.VerifyProtocolsMenuAsync`
   constrói `FrmIntegracoes` com `monitorAcm`, `contractsRead`, `carriersRead` e `protocolsRead`,
   **sem** passar `toledoRead`. É lacuna do verificador, não do aplicativo.
2. Eu disse que o 007 resolveria o 503. Os GRANT eram necessários, mas **não suficientes**.

### O quarto guard de identidade

`/dev/read/toledo` seguia em 503 porque `RecebimentosToledoPostgreSqlReadRepository.Protect()`
validava a identidade com `SqlIdentidadeAcm`, o predicado fixo em `fugates_acm_hml_app`, lançando
`TOLEDO_READ_DATABASE_IDENTITY_REJECTED`. Era o quarto ponto de recusa — nasceu depois da minha
correção dos outros três, em arquivo do Codex.

Decisão do operador: **não tocar no arquivo dele**. E foi acertada: enquanto eu preparava o patch,
o Codex aplicou a correção sozinho, usando exatamente o mesmo padrão
(`SqlIdentidadeRole` com `@role`, caindo em `SqlIdentidadeAcm` quando a role esperada é nula), e já
havia acrescentado `roleEsperada` ao construtor, repassado por `DevUnifiedReadStore`.

**O fonte está correto; o 503 persiste apenas porque o pacote implantado no 18762 é de 21:13 UTC e
antecede essa correção.** Nenhuma reimplantação foi feita: ela pertence à janela conjunta, quando a
frente Toledo do Codex estiver concluída e a regressão verde.

### Regressão final

Build 0 erros. `R158.SapInboundReadiness.Tests` **149/0**. `FugaTES.Testes` 1631/2.

As duas falhas são do Codex e apontam para o mesmo arquivo novo,
`src/FugaTES.Integracao.SAP/Http/DocumentoToledoSapGateway.cs`, por conter `HttpMethod.Post`:

- `Seguranca_CodigoProdutivo_NaoContemRegressoesDoIncremento001`
- `SegurancaInc005_NaoContemTransporteRealCredenciaisOuDdlExecutado` (hash do código produtivo)

São guards de **segurança** que proíbem transporte real nessa camada. Vale avaliação antes de
consolidar a frente Toledo.
