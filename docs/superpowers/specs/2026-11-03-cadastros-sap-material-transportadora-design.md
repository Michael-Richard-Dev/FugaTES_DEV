# Consulta SAP de Material e Transportadora

Data:2026-11-03. Aplicar o padrao Emissor aprovado pelo operador. SAP e origem, Guardian destino futuro e FugaTES e integracao/monitoramento. Sem cadastro mestre local, banco, workers, chamadas Guardian, POST ou deploy VM.

## Escopo aprovado

Material e Transportadora: lista, filtro codigo/nome, paginas50, dados detalhados e JSON SAP com fonte real. Motorista e Veiculo aguardam codigo/contrato SAP, conforme operador; nao presumir empregado= motorista ou placa de remessa=cadastro de veiculo. Preservar suas telas e a tela Emissor1.2.16. Claude pausado/reserva DEV110 confirmada.

## Fontes comprovadas

- API_PRODUCT_SRV/A_Product, to_Description, to_Plant, to_ProductUnitsOfMeasure. MetadadosGET200 e coleta limitadaGET200:1784materiais. DescricoesPT disponiveis na API; nao converter unidade/pesos nem fabricar descricao ausente.
- API_BUSINESS_PARTNER/A_BusinessPartnerRole filtroCRM010:46linhas, GET200. Navegacao Role->BP inexistente nos metadados; resolver os BPs pelos codigos exatos com consultas GET limitadas. Nao filtrar como fornecedor generico. Reutilizar parserOData e politica temporalCRM010 existentes, sem autorizar escrita.

## Abordagem

Reutilizar padrao leitura e autenticacao ja aprovado, sem alterar emissor ou motor de negocio. Leitores novos buscam paginas do SAP ate obter catalogo completo limitado, retido somente na memoria da tela; paginacao/filtros globais do resultado dessa consulta. Atualizar consulta gera nova leitura. Nenhum arquivo de cadastro/checkpoint ou tabela local. Falha de pagina rejeita a leitura inteira, nunca apresenta catalogo parcial como completo.

Tamanho de50na UI. Produtos lidos em paginas50com campos basicos+descricoes; codigo pode restringir consultaSAP, nome aplicado no catalogo lido, sem supor navegacao inexistente emDescription. Transportadoras:enumerarCRM010e hidratar BP em lotes pequenos; nome aplicado ao catalogo completo. Snapshot de leitura temporario nao e cadastro mestre. Cursor vinculado a consulta/categoria, sem aceitar URL de continuacao externa. Lista de BP preserva zeros e chaves textuais exatas.

Detalhe Material:JSON original com dados gerais, descricoes, centros e unidades de medida. Detalhe Transportadora:BP, papeis, documentos, enderecos, usos, telefones/e-mails. Mostrar todos campos escalares disponiveis dessas expansoes; ausentes permanecem ausentes. SomenteGET HTTPS origemSAP aprovada/client110, timeout, limite de corpo e cancelamento ao fechar. Segredos pelo provider existente; dados pessoais e payloads fora de logs/evidencias/pacote.

## Arquivos

Novos: Aplicacao/Consultas/CadastrosSap/CadastrosSapConsulta.cs; Integracao.SAP/Cadastros/SapCadastroLeitura.cs, MateriaisSapConsultaAdapter.cs e TransportadorasSapConsultaAdapter.cs; WinForms/Cadastros/FrmCadastroSap.cs, Designer.cs e CadastrosSapComposition.cs. Ajustes de rota em FrmCadastros.cs e FrmIntegracoes.cs. Manter entrada legada Transportadoras e sua funcionalidade intactas. Versao somente ao publicar.

## Validacao e publicacao

TDD de pagina/filtro/descricaoPT/chaves/detalhe/CRM010vigencia e autenticacao GET-only, cursor/cancelamento/falhas nao sao ausencia. Tela em STA com fontes sinteticas, regressao de Emissor e navegacao. BuildRelease, teste binario exato, leitura fisica sanitizada e layout QA. BackupDEV110 e instalacao local coordenada somente aplicativo fechado, semconfig/credenciais no ZIP. Sem commit/push ou alteracaoVM.
