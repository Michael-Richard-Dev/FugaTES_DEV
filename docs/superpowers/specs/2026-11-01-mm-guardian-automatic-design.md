# MM — criação automática de protocolos Guardian por item de pedido de compra

Data: 01/11/2026. Estado: desenho para aprovação; nenhum POST, DDL ou deploy executado.

## Decisões confirmadas pelo operador

- Repositório único: FugaTES_DEV oficial; preservar os incrementos de Claude, ACM, SD e Toledo.
- Reutilizar como referência o fluxo SD concluído; trocar ordem de venda por pedido de compra.
- SAP cliente 110, empresa 3000, centros 3011/3008, todos os tipos de pedido; manter os filtros atuais de atividade e liberação concluída.
- Consulta a cada 30 segundos; continuar com o WinForms fechado, enquanto a estação e a sessão TI estiverem ativas, pelo coletor MM existente.
- Incluir também os pedidos elegíveis já consultados, além dos novos; não confundir inclusão no escopo com prova de ausência no Guardian.
- Um protocolo por item. Os valores 1/2/3 fornecidos eram exemplos, não constantes empresariais.
- Endpoint: https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro.

## Abordagem

Adaptar o caminho SD de composição, autenticação, reserva durável anterior ao POST, captura do resultado e monitoramento, com tipos e tabela próprios do MM. A execução fica no coletor MM, não no evento da tela: é a diferença necessária para manter o requisito de funcionar com a interface fechada.

Alternativas consideradas: executar o envio no Timer do Form, como o SD, encerraria a automação ao fechar a tela; hospedar outro serviço na VM ampliaria o escopo sem necessidade demonstrada. Não alterar a execução SD nem fazer refatoração global para compartilhar orquestradores.

## Mapeamento

| Campo | Origem/valor |
| --- | --- |
| dadosProtocoloPreCadastro.id | 0; o Guardian atribui o protocolo, como no SD |
| codigoItem | Material do item SAP |
| codigoEmissor | Supplier do cabeçalho SAP |
| camposAdicionais numero 1000 | E |
| 1001 | Plant real do item (3011 ou 3008) |
| 1002, 1003, 1004 | 0 |
| 1005 | PurchaseOrder real |
| 1006, 1007, 1008 | 0 |
| 1009 | PurchaseOrderItem real, não a posição na grade |

Os campos adicionais usam objetos numero/valor, conforme o DTO SD existente. Preservar os identificadores originais no snapshot SAP; a normalização para o Guardian remove somente zeros de preenchimento em códigos numéricos, seguindo a regra SD, e conserva a equivalência na chave de duplicidade. Nunca substituir material, fornecedor ou centro pelos exemplos.

O envelope inclui configuracao.urlParaRetorno conforme o contrato existente. O SD ainda contém uma URL inicial de captura externa: não copiá-la como retorno MM. A URL MM precisa ser configurada e validada antes da ativação real; não inventar rota nem habilitar devolutiva MM neste incremento.

## Execução e persistência

1. Consultar todas as páginas SAP; uma consulta parcial ou com erro não admite itens novos para envio. Revalidar elegibilidade no ciclo antes de criar a reserva.
2. Mapear e validar o item; montar e serializar uma única vez os bytes do POST, que também serão exibidos no botão JSON Guardian.
3. Validar configuração Guardian pelo provider existente, HTTPS, origem e rota exatas, timeout e redirecionamentos bloqueados. Segredos fora do snapshot, pacote e logs.
4. Usar PostgreSQL DEV 5433, fugates_dev/schema fugates, com registro próprio MM. Preparar migration aditiva e rollback como candidatos, sem executá-los nesta fase.
5. O binding MM deve usar identidade aplicacional nominal autorizada, com privilégios mínimos da tabela MM. Não reutilizar fugates_dev_app nem ampliar permissões SD/ACM por pressuposto. Verificar cluster 18/fugatesacmdev, porta física 5433, banco e usuário efetivos antes de qualquer escrita.
6. Reservar atomicamente a chave MM (empresa/pedido/item canônicos) com índice único antes do POST. Registrar payload e hash exatos, origem, centro, versão e timestamps. A lease local impede dois coletores da sessão; a unicidade no banco impede competição entre instalações.
7. Transmitir uma vez. Confirmar criação somente com HTTP compatível, resposta empresarial sem erro e protocolo válido. HTTP 2xx sem identificação suficiente permanece indeterminado.
8. Persistir o resultado; falha ao gravar depois do POST não libera a chave. Timeout, desconexão ou queda deixam a reserva bloqueada para reconciliação; sem retry automático empresarial.
9. Pedidos já concluídos, recusados ou indeterminados não são recriados a cada consulta. Alterações posteriores de um pedido já enviado são sinalizadas na tela; não criar outro protocolo ou atualizar o remoto neste incremento.
10. Processar sequencialmente em lotes de até dez itens por ciclo, a exemplo do SD; preservar leitura a cada 30s sem sobreposição. A implementação deve separar o agendamento da coleta da fila sequencial de envio, para que dez POSTs lentos não congelem a coleta. Usar o último ciclo completo válido para admissão, nunca um cache antigo como autorização.

Estados mínimos: aguardando, envio reservado/indeterminado, criado, recusado e bloqueado por configuração/dados. Nenhum botão poderá disparar HTTP diretamente do Form.

## Lote inicial e duplicidade remota

A autorização inclui os pedidos atuais. Antes da ativação, conferir se há protocolos MM anteriores, manuais ou de outra instalação, associados ao mesmo pedido/item. Não declarar ausência porque a tabela MM nova está vazia. Reutilizar consulta documentada ou evidência de conciliação legítima; se o contrato de consulta não estiver disponível, identificar essa pendência sem inventar endpoint. Itens não conciliados permanecem visíveis e bloqueados, sem impedir a implementação e os testes offline.

## Tela

Preservar filtros e JSON SAP. Adicionar JSON Guardian, estado do envio, HTTP, protocolo Guardian, última tentativa e mensagem segura. Exibir claramente automação ativa/inativa/bloqueada e a situação do coletor. A tela apenas acompanha o coletor; fechar/reabrir não cria um segundo iniciador. Sem token ou credencial nos JSONs exibidos.

## Arquivos e limites

Alterações previstas: modelos/serviço de envio em Aplicacao/Mm; adapter Guardian MM baseado no SD; registro PostgreSQL MM; composição/coletor/snapshot MM; FrmPedidosCompraMm.cs e seu Designer somente se necessário; testes focais e SQL candidato em docs/Incrementos/007. Reutilizar mecanismos de autenticação e túnel existentes sem mudar os arquivos SD ou os seus registros. Não usar o caminho GET-only da API privada para comandos de escrita.

## Verificação e entrega

Testes offline com transports simulados: mapeamento real de pedido/item, Supplier/Material, dez campos, paginação/escopo, primeira coleta incluindo existentes, idempotência entre ciclos, competição de reservas, falta de banco sem POST, queda após reserva, HTTP 400, timeout, 2xx sem protocolo, gravação de resultado, alteração de item já enviado, fechamento/reabertura da tela e regressão dos caminhos compartilhados. Build Release; nenhuma chamada empresarial de teste.

Entregar pacote versionado, manifesto e SHA-256, SQL candidato e permissões requeridas, configuração sem segredo, plano de publicação/backup/rollback. Instalação, DDL/GRANT, provisionamento e ativação do lote real serão apresentados de forma concreta antes de execução; não estão sendo executados ao documentar ou testar este desenho.

## Dependências para ativação real

- Definir binding nominal MM e aplicar a estrutura aditiva mediante autorização específica.
- Fixar a URL de retorno MM no envelope sem copiar o webhook de teste SD.
- Comprovar a situação dos protocolos preexistentes para admitir o lote inicial sem duplicidade remota.
- Coordenar publicação local com Claude; preservar EXE e incrementos paralelos não reconciliados.

## Adendo confirmado pelo operador
Em 01/11/2026, o operador autorizou usar temporariamente a mesma URL de captura configurada no SD. O exemplo MM replica essa referência com Enabled=false. Isso autoriza somente o destino de retorno no envelope; não instala handler MM nem despacha o retorno pelo pipeline SD/ACM. A conferência de protocolos preexistentes e a ativação operacional permanecem pendentes.
