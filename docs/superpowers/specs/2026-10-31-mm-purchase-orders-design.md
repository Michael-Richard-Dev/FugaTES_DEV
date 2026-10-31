# MM — consulta de pedidos de compra SAP no WinForms

Status: proposta para confirmação do operador; sem implementação funcional MM.
Data: 31/10/2026. Baseline observada: 86b74051cb2e1368b6e475b0717773bfe9319fbe (SD).

## Objetivo e escopo
Consultar pedidos SAP da empresa 3000, centros 3011 e 3008, ativos e aprovados,
apresentando pedido, item, centro, material, grupo de material, quantidade decimal,
unidade e fornecedor. Buscar novamente a cada 30 segundos sem concorrência de ciclos.
Primeira etapa somente leitura: sem criação de pedido, Guardian, envio SAP, banco,
migration, serviço VM, merge, push ou mudança nas frentes ACM/Toledo/SD.

## Evidência técnica já verificada
- PDF fornecido: `01.Pedido de Compras - API PurchaseOrder [FUGA] (1).pdf`,
  SHA256 5E64307932FDBEB39460BE3FEF087DD7A0FD6797A63CAE0BD70423726713331A.
- GET autenticado do `$metadata` real em 31/10/2026: HTTP 200, teste PASS.
- Origem aprovada existente: https://vhfufds4ci.sap.fugacouros.com.br:44300,
  sap-client=110; perfil FugaTES.carrier.json e provider Windows existente.
- Rota OData V4: /sap/opu/odata4/sap/api_purchaseorder_2/srvd_a2x/sap/purchaseorder/0001/.
- PurchaseOrder contém CompanyCode, Supplier, PurchaseOrderType,
  PurchaseOrderDeletionCode, PurchasingProcessingStatus, ReleaseIsNotCompleted,
  PurchasingCompletenessStatus e navegação _PurchaseOrderItem.
- PurchaseOrderItem contém Plant, Material, MaterialGroup, OrderQuantity,
  PurchaseOrderQuantityUnit e PurchasingDocumentDeletionCode.
- Supplier será lido do cabeçalho, não presumido como campo de item.
- Guia SAP dos códigos: https://userapps.support.sap.com/sap/support/knowledge/en/3416344.
- Semântica dos indicadores: https://userapps.support.sap.com/sap/support/knowledge/en/3070935.
- Propriedades V4: https://help.sap.com/docs/SAP_S4HANA_ON-PREMISE/91af7f8d3acd47da90d33aaacfcd0d59/10d0eb23998e4622b50a5f2a84fba8e4.html.

## Abordagens e decisões pendentes
1. Recomendada para esta primeira tela: consulta direta GET pelo provider SAP existente,
   automática enquanto a tela MM estiver aberta, como a tela SD já integrada. Não cria
   backend nem depende de botão para cada ciclo. Inicia uma consulta ao abrir e depois
   a cada 30 segundos; permite pausar/atualizar. Fechar cancela e encerra a consulta.
2. Consulta permanente com o WinForms fechado: exigiria execução em backend, ciclo de
   vida, credencial de serviço e readback persistido. É uma escolha adicional, não
   equivalente a um Timer WinForms. Não instalar serviço por presunção.
3. Reutilizar o fluxo empresarial ACM/worker: descartado; pedido de compra não é
   contrato ACM e esta etapa não deve despachar operações empresariais.

Pendentes com o operador: todos os tipos ou somente ZCOL; consulta apenas com a tela
aberta ou contínua com o aplicativo fechado. Proposta: todos os tipos (pedido literal
do usuário), tela aberta (menor incremento compatível com o SD). Esses padrões são
propostos pelo Codex, não decisões atribuídas ao operador.

## Critério explícito de elegibilidade proposto
- CompanyCode = 3000; item Plant em 3011 ou 3008.
- PurchasingProcessingStatus = 05 (Release Completed).
- ReleaseIsNotCompleted = false; PurchasingCompletenessStatus = false.
- PurchaseOrderDeletionCode vazio; PurchasingDocumentDeletionCode do item vazio.
- Status desconhecido ou indicadores obrigatórios ausentes não são classificados
  como aprovados. Exibir erro de contrato se faltarem propriedades necessárias.
- Não confundir status 02 (Active) com prova de aprovação. Tipos sem processo de
  liberação, caso devam entrar, precisam de regra empresarial confirmada; não
  ampliar automaticamente o critério aprovado.
- Não excluir itens somente por entrega completa: “em aberto para entrega” não foi
  pedido. Não usar o centro 4000, o material ou fornecedor do exemplo como padrão.

## Contrato da leitura e completude
Consultar cabeçalhos com itens (_PurchaseOrderItem); filtro de empresa e aprovação,
projeção apenas dos campos necessários. Filtrar centros em cada item e conferir os
critérios também localmente. Percorrer todas as páginas de cabeçalho e de itens quando
existirem; nunca anunciar “todos” após truncar uma página ou atingir limite de proteção.
Ordem determinística por pedido/item. Códigos como strings, zeros preservados;
quantidades como decimal invariável, aceitando representação JSON decimal string/number.
Fornecedor vem do cabeçalho. Cada JSON exibido identifica a origem e é projeção de
cabeçalho + item efetivamente lidos, não uma solicitação de criação nem payload completo.

Transporte exclusivamente GET, HTTPS, origem/porta/client/rota exatos aprovados;
sem redirects, sem envio de credencial a nextLink externo. Paginação valida origem,
rota e cliente antes de autenticar; detecta links repetidos, páginas conflitantes,
limites de bytes/páginas/duração e resultado parcial. Nenhum retry automático de erro.
Ausência de permissão, 401/403, falha de rede ou schema incompatível aparecem como
falha objetiva sanitizada, nunca como zero pedidos ou dados simulados.

## Tela e ciclo de vida
Menu Integrações > Pedidos de compra (MM) — leitura SAP. Empresa/centros fixos e
critério de aprovação visível. Grade somente leitura com as nove informações pedidas,
tipo/status e JSON. Status CONECTADO/DESCONECTADO, client 110, última atualização,
contagem de pedidos/itens e novos/alterados desde o ciclo anterior da mesma sessão.
Primeira leitura ao abrir; consulta a cada 30 segundos com um único ciclo em voo.
Em falha preservar a última grade boa explicitamente como DESATUALIZADA; não publicar
snapshot parcial. Sem histórico durável ou promessa de leitura com a tela fechada no
modo recomendado. Cancelamento no fechamento, descarte do HttpClient/timer e provider
existente. Separar Form.cs e Designer.cs, sem lógica de negócio no Designer.

## Limites de arquivos previstos
Criar modelo/resultado e porta MM em FugaTES.Aplicacao; adaptador V4 paginado em
FugaTES.Integracao.SAP/BusinessDocuments; composição MM e FrmPedidosCompraMm.cs /
FrmPedidosCompraMm.Designer.cs em FugaTES.WinForms; testes focais no projeto existente.
Alterar somente a integração de menu em FrmIntegracoes.cs e .Designer.cs e, se
indispensável, sobrecarga limitada de GET no executor SAP sem alterar contratos atuais.
Nenhuma modificação dos componentes SD, do SAP writer, perfis secretos ou banco.

## Verificação e publicação
Testes simulados: escopo/aprovação/deleção; campos obrigatórios; quantidade/zeros;
paginação completa e links forjados; GET-only; atualização sem duplicação, sem
concorrência e com cancelamento; falha preserva snapshot anterior; menu e JSON.
Build Release, regressões focais dos caminhos compartilhados e revisão independente.
Consulta real GET no escopo autorizado e validação visual, sem POST. Uma única
publicação DEV110 coordenada com Claude SD: confirmar ausência de publicação concorrente,
backup integral, conferir pacote/hashes, abrir EXE oficial e preservar rollback.
Não alegar sucesso físico ou visual apenas pelo resultado de teste simulado.

## Critérios de aceite
Tela acessível no WinForms oficial; dados SAP reais de empresa/centros corretos,
ativos/aprovados; paginação integral; repetição 30s sem sobreposição; mensagens claras;
JSON por item; build/testes PASS; SD/ACM/Toledo preservados; nenhuma escrita empresarial.
Se não houver registros elegíveis, mostrar zero somente após consulta completa PASS.

## Decisão do operador e desenho final — 31/10/2026
Operador confirmou todos os tipos e o desenho MM, especificando que a consulta deve
continuar com o sistema fechado. Essa resposta específica substitui a proposta de
Timer limitado à tela. A implementação utilizará o próprio FugaTES.exe em modo
headless --mm-purchase-orders, com uma instância por diretório privado do usuário,
independente da janela principal. Reutiliza a credencial Windows TI que já obteve
metadata HTTP 200. Não necessita de serviço VM, senha nova, banco ou API adicional.
O processo é iniciado na instalação/abertura da tela e por tarefa no logon de TI,
sem privilégio elevado; continua após fechar o WinForms. Depende de estação ligada
e sessão TI conectada. Desligar a estação encerra o coletor, como qualquer processo
local; consulta permanente sem estação exigiria outra publicação, não incluída.
Snapshot completo é salvo atomicamente em LocalAppData/FugaTES/DEV110/mm-purchase-orders,
com ACL do usuário/SYSTEM, fora do Git/pacote; último snapshot bom permanece visível
em erro, sinalizado como desatualizado. Uma trava de arquivo impede coletores
concorrentes, inclusive entre sessões Windows do mesmo usuário. Sem listener HTTP.
Nenhuma consulta é disparada pela grade: ela apenas acompanha o coletor e oferece JSON.
