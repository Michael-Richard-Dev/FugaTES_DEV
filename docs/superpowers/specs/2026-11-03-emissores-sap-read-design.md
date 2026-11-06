# Emissores SAP: consulta e monitoramento da integracao

Data: 2026-11-03
Estado: desenho para aprovacao; sem implementacao de produto nesta etapa.

## Objetivo e limites

SAP e a origem autoritativa dos cadastros; Guardian sera o destino; FugaTES orquestra e monitora a integracao. Nao criar cadastro mestre, manutencao manual ou tabela de emissores no FugaTES. A primeira entrega consulta SAP e exibe dados; nao cadastra nem altera Guardian.

Escopo solicitado: clientes e fornecedores, PF/PJ, incluindo propriedades. O BP 20000170 indicado pelo operador foi consultado por um GET autenticado em 03/11/2026: HTTP 200, possui to_Customer e to_Supplier. Incluir esse BP uma unica vez e mostrar suas duas funcoes. Nao inferir que todo grupo Z2 seja propriedade nem classificar todos os demais BPs a partir desse exemplo. Propriedades que possuam os papeis cliente/fornecedor entram pela mesma consulta; outra categoria tecnica exige evidencia propria antes de filtros exclusivos.

## Abordagens

1. Consulta de um BP por vez: reaproveita o mecanismo atual, mas nao atende a visao ampla desejada.
2. Lista paginada com filtros: selecionada pelo operador. Reutiliza API_BUSINESS_PARTNER e o provider protegido existentes, acrescentando uma tela de leitura na navegacao Cadastros -> Emissor.

## Comportamento da primeira entrega

- Consulta manual/atualizacao da lista; sem novo coletor, servico ou agendamento.
- Escopo clientes/fornecedores do SAP configurado, sem presumir restricao de empresa/centro a partir dos fluxos MM ou ACM.
- Paginacao explicita e filtros por codigo BP e nome; mostrar PF/PJ, funcoes cliente/fornecedor, bloqueios informados pelo SAP e horario da leitura.
- Mesmo BP com cliente e fornecedor aparece uma unica vez. Nao confundir BP pessoa fisica com empregado nem incluir apenas empregados como emissores.
- Botao JSON SAP faz leitura detalhada do BP selecionado para conferencia; nao prepara POST Guardian.
- Informar falha de autenticacao, indisponibilidade ou resposta invalida; nao mostrar simulacao ou ausencia de registros como substituto silencioso de falha.
- Dados em memoria durante a consulta. Sem persistencia de cadastro mestre; nenhuma nova estrutura PostgreSQL.
- Guardian permanece NAO CONSULTADO nesta etapa. Consulta SAP nao e prova de cadastro ou sincronizacao Guardian.

## Integracao e seguranca

- Reutilizar credencial SAP referenciada por FugaTES.carrier.json por meio de CarrierServiceSapAuthentication. Nenhuma senha no fonte, pacote, chat ou logs.
- GET HTTPS exclusivamente no SAP aprovado, mandante 110. Sem redirects, sem POST ou CSRF de escrita, timeout controlado, cancelamento ao fechar a tela.
- Validar continuidade OData antes de enviar Authorization: mesma origem, entidade autorizada e mandante; rejeitar navegacao externa, repetida ou invalida. Nao apresentar pagina parcial como lista completa.
- Preservar texto dos codigos e zeros; nao inventar nomes, grupos, funcoes ou equivalencias.
- Logs/evidencias tecnicos sem payload pessoal completo, documentos fiscais, Authorization, cookies ou segredos. JSON completo somente na tela solicitada pelo operador, sem exportacao automatica.

## Arquivos previstos

Novos:
- src/FugaTES.Aplicacao/Consultas/EmissoresSap/EmissoresSapConsulta.cs
- src/FugaTES.Integracao.SAP/BusinessPartner/EmissoresSapConsultaAdapter.cs
- src/FugaTES.WinForms/Cadastros/FrmEmissoresSap.cs
- src/FugaTES.WinForms/Cadastros/FrmEmissoresSap.Designer.cs
- src/FugaTES.WinForms/Cadastros/EmissoresSapComposition.cs
- tests/R158.SapInboundReadiness.Tests/EmissoresSapConsultaTests.cs
- tests/R158.SapInboundReadiness.Tests/EmissoresSapScreenTests.cs

Ajustes focais: abertura da categoria Emissor em FrmCadastros.cs e FrmIntegracoes.cs; metadados de versao apenas ao preparar publicacao.

## Validacao e entrega

Testes de paginacao, filtros, identidade BP, BP com duas funcoes, bloqueios, resposta vazia versus falha, cancelamento, protecao da continuacao OData e transporte somente GET. Testes da navegacao e JSON da tela com transporte simulado; build Release. Consulta real somente leitura para validar o exemplo 20000170 e o formato da lista.

Preparar candidato local e evidencias. Publicacao do DEV110 exige reserva contra alteracoes concorrentes do Claude e backup; preservar perfis, credenciais e rollback. Sem commit/push, migration, GRANT, deploy Linux, ativacao de worker ou escrita empresarial.

## Proxima etapa separada

Depois da consulta validada, definir mapeamento e envio controlado ao Guardian com identidade, idempotencia, resultado e journal tecnico. Nao habilitar esse envio neste incremento.

## Complemento solicitado pelo operador

Mostrar o maximo de dados SAP para futura integracao de Emissor Guardian: nome, razao social, CPF/CNPJ/IE, todos os enderecos e usos, numero/complemento/municipio/UF/CEP/pais, telefones/e-mails, papeis, grupos e bloqueios. Ausencia de campo permanece ausencia; sem criar valores para sequencial, estado, tipoEmissor, tipoDocumento, UF numerica ou relacionamento de transportadora do Guardian. Campos obrigatorios do print Guardian: voConfiguracao, entidadeEmissor, codigo, tipoEmissor, razaoSocial e descricao; a consulta SAP nao produz POST nem declara elegibilidade completa Guardian.

Ruling tecnica apos GET real: chaves vinculadas Customer/Supplier podem conter letras e diferir do BP. Preservar identificadores como textos; aliases de zeros somente em BP integralmente numerico; escapar codigo literal tanto no filtro quanto na URI de detalhe. Colecoes expandidas ausentes/null sao detalhe incompleto; somente results vazio comprova ausencia.
