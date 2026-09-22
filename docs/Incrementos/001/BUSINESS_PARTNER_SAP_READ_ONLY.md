# Incremento 001 - SAP Business Partner Read Only

## Contrato Congelado

SERVICE=API_BUSINESS_PARTNER
ODATA=V2
GET_ONLY=SIM
Cliente=to_Customer
Fornecedor=to_Supplier
Transportadora=BusinessPartnerRole CRM010 vigente

O Incremento 001 implementa consulta read-only de Business Partner SAP para cliente, fornecedor e transportadora. A implementacao define ports na camada de Aplicacao, adapters na camada FugaTES.Integracao.SAP, DTOs internos de OData V2, mapeamento de contrato e testes automatizados sem chamada real ao SAP.

Nao implementa cadastro, alteracao, exclusao, sincronizacao em lote, persistencia, SQL, UI WinForms, composition root funcional, autenticacao concreta ou credenciais reais.

SAP_REAL_EXECUTADO=NAO

## Estado Dos Gates

GATE_001_A=APROVADO
CONTRATO_E_DESENHO

GATE_001_B=IMPLEMENTACAO_ISOLADA
EM_AUDITORIA

GATE_001_C=SMOKE_SAP_REAL_READ_ONLY
FUTURO
NAO_AUTORIZADO

SAP_REAL_EXECUTADO=NAO
INCREMENTO_001_CONCLUIDO=NAO

## Aplicacao

A camada FugaTES.Aplicacao possui contratos de gateway para consulta de cliente, fornecedor e transportadora SAP. Os casos de uso validam entrada nula ou em branco e delegam a consulta ao contrato correspondente.

A Aplicacao expoe modelos neutros para parceiro de negocio, documento fiscal normalizado, endereco e resultado da consulta. Nenhum tipo HTTP, DTO SAP ou detalhe OData atravessa a fronteira da Aplicacao.

## Integracao SAP

A camada FugaTES.Integracao.SAP contem o cliente HTTP estrutural, os adaptadores de consulta, os DTOs internos de Business Partner e os mapeadores. O cliente HTTP recebe HttpClient por injecao e nao instancia HttpClient diretamente.

A configuracao SAP fica representada por OpcoesSap, com BaseUrl, SapClient opcional e Timeout. O incremento nao define endpoint operacional, nao define usuario, nao define senha e nao adiciona mecanismo concreto de autenticacao.

## HTTP

A consulta usa GET. O contrato mapeia respostas HTTP para status internos:

- 200: tenta desserializar e mapear o payload.
- 400: FalhaContrato.
- 401: FalhaAutenticacao.
- 403: FalhaAutorizacao.
- 404: NaoEncontrado.
- 5xx e falhas tecnicas: FalhaTecnica.
- Configuracao ausente ou invalida: FalhaConfiguracao.

Cancelamento solicitado pelo chamador e propagado. Timeout interno e falhas de transporte sao convertidos para FalhaTecnica.

## DTO e OData V2

Os DTOs SAP permanecem internos ao projeto de Integracao SAP. A resposta esperada usa envelope OData V2 com propriedade d e colecoes com results.

Colecoes paginadas por __next sao tratadas como FalhaContrato para evitar decisao parcial sobre dados incompletos.

Formato temporal OData V2 previsto para as regras de vigencia:

- /Date(milliseconds)/
- /Date(milliseconds+offset)/
- /Date(milliseconds-offset)/

Parser dedicado ja existe. As regras de vigencia usam referencia temporal deterministica. O shape fisico real devera ser comprovado no Gate 001-C. Este documento nao afirma que esse formato ja foi observado no SAP FUGA real.

## Documentos Fiscais

Campos do contrato congelado:

- BPTaxType
- BPTaxNumber
- BPTaxLongNumber

Fiscal:

- BR1=CNPJ
- BR2=CPF
- BR3=IE

Regra fail-closed:

- somente BPTaxNumber preenchido: usar BPTaxNumber.
- somente BPTaxLongNumber preenchido: usar BPTaxLongNumber.
- ambos preenchidos e iguais: usar valor.
- ambos preenchidos e diferentes: FalhaContrato.
- ambos ausentes: documento fiscal ausente.

CNPJ permanece string e pode ser alfanumerico. Nao remover pontuacao, nao remover letras, nao converter para numero, nao alterar caixa e nao escolher precedencia silenciosa.

## Transportadora

Transportadora e identificada exclusivamente pelo role CRM010 ativo na data de referencia. Periodos usam inclusao nas bordas. Ausencia de CRM010 ativo retorna CategoriaNaoCorrespondente; datas invalidas ou inconclusivas retornam FalhaContrato.

Fornecedor SAP sem CRM010 ativo nao prova transportadora. Cliente SAP sem CRM010 ativo nao prova transportadora.

## Endereco

O endereco considerado e o uso XXDEFAULT ativo. Nenhum endereco default retorna sucesso sem endereco principal. Mais de um default ativo ou data invalida no default gera FalhaContrato.

Telefone e email sao opcionais e removidos por duplicidade preservando comparacao ordinal.

## Autenticacao

IAutenticacaoSap=ABSTRACAO_IMPLEMENTADA
AUTH_CONCRETA=NAO_IMPLEMENTADA_NAO_COMPROVADA
BASIC_LEGADO=EVIDENCIA_HISTORICA_SOMENTE
BASIC_ATUAL=NAO_COMPROVADO

Basic legado nao e contrato atual do FugaTES. Nenhuma credencial real e registrada neste documento.

## Sap Client

SapClient=OPCIONAL_NO_GATE_001_B

Se configurado, o cliente HTTP envia o parametro sap-client. Se ausente, o parametro sap-client nao e enviado. Nao existe default hardcoded.

Obrigatoriedade real de sap-client=PENDENTE_DE_COMPROVACAO_GATE_001_C

Nenhum numero real de client e registrado neste documento.

## Fronteiras Fora Do Incremento 001

TOLEDO_NO_INCREMENTO_001=NAO
HOST_FUNCIONAL_SAP_INC001=NAO
WINFORMS_FUNCIONAL_SAP_INC001=NAO
PERSISTENCIA_INC001=NAO
POSTGRESQL_INC001=NAO

Nenhuma consulta Toledo faz parte do Incremento 001. HostIntegracao, WinForms, Persistencia e PostgreSQL nao participam funcionalmente deste incremento.

## Seguranca

O incremento nao contem credenciais reais, Basic/Bearer hardcoded, endpoint operacional SAP ou chamada real ao ambiente SAP. Os testes usam handler HTTP fake e URI nao operacional apenas para montagem estrutural da requisicao.

Fixtures e documentacao permanecem sinteticas. Nao registrar endpoint real, SAP host real, BusinessPartner real, CNPJ real, CPF real, IE real, username, senha, Authorization, token ou client real.

## Testes

A suite automatizada cobre casos de uso, status HTTP, falhas de contrato, documentos fiscais, datas OData V2, papel CRM010, endereco XXDEFAULT, cancelamento, timeout, ausencia de DTO SAP fora da Integracao SAP e ausencia de verbos HTTP mutaveis na implementacao.

A REV2 focal acrescenta cobertura para timeout durante leitura do body e BusinessPartner ausente, nulo, vazio ou whitespace retornando FalhaContrato.

## Limites Declarados

Este incremento prepara a consulta read-only e seu contrato tecnico. A ligacao em composition root, configuracao real, autenticacao real, observabilidade operacional, smoke SAP real e consumo por telas ou fluxos de negocio ficam fora deste incremento.