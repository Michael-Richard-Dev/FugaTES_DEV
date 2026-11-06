# MM — recuperação controlada de criação Guardian

Status: desenho aprovado; implementação e testes offline concluídos; publicação/SQL aguardam autorização operacional. Nenhuma nova tentativa empresarial autorizada/executada por este documento.

## Solicitação
O operador acompanha pedidos de compra no WinForms e precisa recuperar falhas de criação Guardian depois de cadastrar emissor/material. Pedido já criado deve ter botão desativado. SAP é origem; FugaTES mantém apenas integração, evidência e comandos, não cadastro mestre.

## Diagnóstico físico em 04/11/2026
- SAP4500002991/item10: empresa3000,centro3008,status05,material1000247,fornecedor20000162,criado03/11. Presente no snapshot MM. Reserva9347333a-8b6d-4315-b1a2-c62fe6ef0f66; GuardianHTTP400 em03/11; estadoRECUSADO,semprotocolo. JSON reservado tem OC4500002991. Corpo detalhado da recusa não foi retido.
- GETGuardian/emissor20000162 e material1000247, assinatura133289: ambos404, consultas existentes legítimas. Comprova bloqueios atuais de cadastro; não reconstrói o corpo histórico doHTTP400.
- SAP4500002994/item10: empresa3000,centro3008,status02,material1000304,fornecedor20000336,criado03/11. Não atende filtro status05 aprovado; não foi perdido na coleta. Não afrouxar filtro nem reenviar antes da elegibilidade SAP.
- EnvioAutomaticoMm ignora todas as chaves já reservadas, inclusiveRECUSADO. O registro existente protege duplicação mas não oferece reenvio. UI não tem açãoMM.

## Abordagem proposta
Reutilizar motor MM na VM, registro PostgreSQL5433, adapters e serviço autenticado de comando18763. Nova ação MM isolada no serviço existente; nenhum serviço/monitor paralelo. A API privada18762 permanece estritamente GET-only. WinForms não terá credenciais Guardian, SQL de escrita ou POST direto ao Guardian.

A ação será por pedido/item, como a grade existente e a identidade de um protocolo. Recuperar item10 não repetirá item20 já criado. Uma ação agregada por pedido acrescentaria lote e estados parciais sem necessidade nesta etapa.

## Fluxo
1. Coleção SAP continua a cada180s, corte inclusivo01/11/2026,empresa3000,centros3011/3008,status05,ativo/completo. Falha de consulta não autoriza envio usando dados antigos.
2. Antes de criar novo protocolo ou recuperar falha, consultar emissor e material com os GETs Guardian já aprovados. Código,assinatura,estadoativo e identidade devem coincidir. Ausente/inativo/consulta não comprovada mantém pendente e informa motivo preciso; nenhum POSTProtocoloPreCadastro nesses casos. Não criar cadastros automaticamente.
3. Grade mostra Reenviar por linha, motivo/etapa, HTTP e protocolo. Falta de cadastro mantém o registro acessível. Dados originais/JSON e resultados anteriores preservados.
4. Clique solicita prévia autenticada: pedido/item,hashes de origem/payload,último resultado,atualidade SAP,estado/identidade dos cadastros,ausência de protocolo e de comando/processamento em aberto. Mostrar confirmação com justificativa e identidade do operador derivada da sessão autenticada.
5. Submit grava comando durável antes de responder aceitação. Transação impede comando concorrente para mesma chave e valida digest da prévia novamente. Duplo clique retorna o mesmo comando ou conflito seguro.
6. O único motor MM, mantendo a lease existente, recebe o comando e revalida elegibilidade/dados/cadastros. Preserva bytes do payload aprovado e numeroReferenciaOC+pedido. Registra início antes do POST, resultado depois. Não usa reset da fila automática nem apaga a reserva original.
7. Ao confirmar protocolo e resultado empresarial, estadoCRIADO e botão desativado. Rejeição conhecida guarda motivo seguro; timeout/5xx/resultado ambíguo ficaINDETERMINADO e bloqueado para reconciliação, sem retry automático.

## Elegibilidade e bloqueios
- Permitido apenas recuperar falha determinística e corrigível; documento/versão de origem atuais e confiáveis,sem resultado externo incerto e sem protocolo confirmado.
- HTTP400 isolado não libera reenvio. Exigir causa/correção comprovadas e ausência de estado externo inseguro. Registro legado sem causa suficiente permanece em conferência até evidência adequada; nunca simular a resposta histórica.
- Para2991 há evidência atual dos dois cadastros ausentes. A implementação deve tornar essa dependência visível; nenhuma tentativa será feita pelo desenvolvimento ou testes. Após correção, nova prévia e confirmação do operador determinarão a elegibilidade efetiva.
- Desativar/bloquear paraCRIADO,INDETERMINADO,processando/comando pendente,SAPforaescopo/dadosdesatualizados,sourcehashalterado,cadastroausente/inativo/nãocomprovado,ambientedivergente ou dados sintéticos. Mensagem objetiva em cada recusa.
- A regra é aplicada novamente no servidor; habilitar visualmente um botão não concede permissão empresarial.

## Persistência e segurança
- Extensão MM aditiva para comandos/tentativas: identificador, chave/reserva, estado/fingerprint anteriores, payload/sourcehash, operadorSID, justificativa, timestamps, claim/ownership, início do envio e resultado/protocolo.
- Preservar registro e payload originais. Resultados de tentativas ficam auditados; falha de persistência não pode virar sucesso na UI.
- Reutilizar rolefugates_mm_app, guard física18/fugatesacmdev/fugates_dev/5433 e credenciais protegidas já aprovadas. O serviço compartilhado recebe apenas bindingMM explicitamente habilitado; demais rotas/políticas preservadas. Sem copiar segredo paraGit/pacote/log.
- Se a estrutura atual não comportar histórico/claim com segurança, preparar migration aditiva e grants mínimos como CANDIDATOS. Não executarDDL/GRANT, mudar serviços nem ativar POST antes de liberação operacional específica com pacote,backup e rollback.
- Diagnóstico Guardian reutiliza leitor seguro R165AD, limitado/redigido; não guardar corpo com token/cookie/credencial ou dados pessoais desnecessários.

## Arquivos/áreas previstas
- Aplicacao/Modelos/Mm e Contratos/Mm: fatos de elegibilidade/comando/auditoria, sem dependência deUI.
- Aplicacao/Servicos/Mm: revalidação e execução controlada, envio automático preservado.
- Integracao.Guardian/GuardianProtocoloMmClient e leitores existentes: prechecksGET e causa segura; nenhuma alteração em SOAP/RESTACM/SD.
- Persistencia/Mm: reserva/commandclaim/resultado transacionais e guard5433.
- HostIntegracao/Mm: consumo dos comandos pelo único motor atual.
- Callback.Http/ManualRetry: rota MM no serviço18763, autenticação existente; GET-only18762 não receberá comandos.
- WinForms/FrmPedidosCompraMm(.Designer quando necessário),MmGuardianMonitorRow e binding de comando: botão, confirmação e atualização.
- Testes MM focais e scriptSQL candidato; preservar fonte oficial, SD, ACM, Toledo, Cadastros1.2.17 pendentes, histórico e demais alterações.

## Validação/instalação
Testes simulados: cadastro ausente/inativo não fazPOST; cadastro corrigido permite tentativa; criado fica desativado;timeout/resultadoincerto e400semcausacorrigida bloqueiam; duplocliqueconcorrente um comando/uma tentativa; perda lease ou persistência não fazPOST/falsosucesso; payload original ehash preservados; auditoria operador/justificativa e história; SIPstatus02 bloqueado; itens do mesmo pedido independentes; autenticação inválida e tentativa de usarGET-only recusadas; semPOSTexternonos testes.

BuildRelease, regressões pertinentes, revisão independente, pacote/hash, instrução de promoção/rollback. Publicação DEV110, serviçoMM/comando e SQL serão apresentados em escopo concreto após testes e coordenação. Não reenviar2991 nem qualquer histórico durante preparação.

## Binding final de política
Preservar ProtectHome=yes no motor MM. Política não secreta via LoadCredential=mm-retry-policy, caminho runtime e CREDENTIALS_DIRECTORY exatos do serviço MM; arquivo0400/0440,diretório0500/0550,sem links,ownerroot/euid e ancestrais seguros. Comando lê fontehome0600 com ProtectHome=read-only existente. Credencial do banco e guards dos demais serviços inalterados.
