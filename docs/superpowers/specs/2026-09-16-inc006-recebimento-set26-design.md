# INC006 Recebimento SET26 Design

## Objetivo

Materializar um vertical slice offline e testavel para o processo `RECEBIMENTO`/`FLX_RECEBIMENTO`: entrada SAP/CPI interna, contratos Guardian SET26, pre-cadastro fake, correlacao em memoria, callback de ticket e saida SAP fake.

## Limites

- Nao alterar contratos legados dos incrementos 002, 003, 004 ou 005.
- Nao implementar transporte HTTP Guardian ou SAP/CPI.
- Nao criar schema ou migration PostgreSQL.
- Nao definir URL, token, credencial ou endpoint default.
- Nao implementar OCR, fotos, eixos, containers, fatores de correcao, qualidade completa ou expedicao.

## Estrutura

O slice sera adicionado aos projetos existentes com namespaces SET26 separados:

- `FugaTES.Aplicacao`: modelos internos SAP, modelos Guardian, ports e casos de uso.
- `FugaTES.Integracao.Toledo`: DTOs/mapeadores SET26 e gateways Guardian fake.
- `FugaTES.Infraestrutura`: repositorio de correlacao em memoria e gateway SAP outbound fake.
- `FugaTES.Testes`: testes focais do fluxo completo.

Os modelos e mapeadores antigos permanecem intactos.

## Entrada SAP/CPI

`RecebimentoSapCpiMvp` contem exclusivamente `Processo`, `CodigoFluxo`, `Material`, `BusinessPartnerEmissor`, `BusinessPartnerTransportadora`, `Contrato`, `DocumentoLogisticoReferencia`, `TipoContrato`, `Centro` e `OperacaoContrato`.

O caso de uso rejeita campos obrigatorios vazios e exige `Processo=RECEBIMENTO` e `CodigoFluxo=FLX_RECEBIMENTO`.

## Guardian SET26

Emissor possui `codigo` opcional de ate 20 caracteres, `descricao` e `razaoSocial` obrigatorios de ate 50 caracteres, `estado` restrito a `"0"`/`"1"` e `tipoDocumento` restrito a `"0"`/`"1"`.

Transportadora possui `codigo` opcional de ate 20 caracteres e `descricao`/`razaoSocial` obrigatorios de ate 50 caracteres.

O pre-cadastro usa `id=0`, `codigoFluxo=FLX_RECEBIMENTO`, material como item, BPs de emissor e transportadora, contrato sem sufixo `C/D`, `idAgenda=1`, documento logistico como `numeroReferencia` e campos exatamente `1000=TipoContrato`, `1002=Centro`, `1003=OperacaoContrato`. `urlParaRetorno` e endpoint sao configuracoes obrigatorias sem defaults. Autenticacao e apenas um port, sem segredo ou implementacao operacional.

## Correlacao e Callback

O pre-cadastro fake retorna um protocolo Guardian. Um repositorio em memoria associa documento SAP, protocolo e posteriormente ticket Guardian.

O callback recebe `Sequencial`, `PesoBruto`, `Tara`, `PesoLiquido`, estado minimo e protocolo de correlacao. O caso de uso vincula o `Sequencial` como ticket e produz `PesagemSapMvp`.

Repeticao equivalente do mesmo ticket e idempotente e nao reenvia ao SAP. Repeticao com dados conflitantes falha fechada.

## Saida SAP

`PesagemSapMvp` contem `SistemaBalanca="Toledo"`, centro, `TipoPesagem="E"`, ticket como `IdPesagem`, agenda quando disponivel, transportadora, material, parceiro, pesos bruto/tara/antes do desconto, tipo de documento e pedido de compras. O gateway SAP outbound e somente fake/port.

## Persistencia e Bloqueios

- `MIGRATION_REQUIRED=NAO`: correlacao in-memory atende ao E2E offline.
- `CALLBACK_ROUTE_BLOCKED=SIM`: nenhuma rota HTTP sera criada.
- `GUARDIAN_REAL_ENDPOINT_BLOCKED=SIM`.
- `SAP_REAL_WIRE_BLOCKED=SIM`.

## Testes

Cobrir entrada valida, pre-cadastro SET26, documento Guardian `0/1`, campos `1000/1002/1003`, ausencia dos legados `C/D` e `1=Interno`, callback e pesos, correlacao, duplicidade idempotente/conflitante, rejeicao de obrigatorios e `PesagemSapMvp` entregue ao fake SAP.
