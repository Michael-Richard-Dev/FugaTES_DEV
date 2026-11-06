# MM — motor contínuo na VM e WinForms como monitor

Data: 01/11/2026. Revisão 2: intervalo e corte de criação aprovados pelo operador. Estado: desenho R2 aprovado pelo operador e implementado/testado offline; instalação e ativação ainda não executadas.

## Objetivo confirmado

O operador desliga a estação Windows e quer que a busca SAP e o POST Guardian continuem na VM vm-fugatesbio. Executar como serviço Linux iniciado no boot, independente de sessão Windows, mantendo o WinForms oficial para acompanhamento.

Preservar SAP cliente110/empresa3000/centros3011,3008/todos os tipos elegíveis, mapeamento E/material/fornecedor/pedido/item, endpoint Guardian e retorno temporário já aprovados. Por decisão expressa do operador nesta revisão, consultar a cada 3 minutos e selecionar somente pedidos criados a partir de 01/11/2026, inclusive; essa decisão substitui o intervalo de 30 segundos e a admissão de pedidos anteriores. Preservar bancoDEV5433/fugates_dev/schemafugates/rolefugates_mm_app. Não migrar ou recriar os registros MM existentes.

## Base comprovada

A instalação1.2.13 processou166 itens:5 protocolos Guardian13–17 confirmados,161 recusados HTTP400,nenhum indeterminado. GETs de catálogo confirmaram23 emissores e29 materiais ausentes. Esses resultados são históricos duráveis e não constituem fila livre para reenvio.

O coletor atual depende de WindowsIdentity, Gerenciador de Credenciais, túnel SSH e cache local. O mapper, o gateway, a reserva PostgreSQL e o adapter SAP já são reutilizáveis. A API privada18762 é GET-only, autenticada por X-R165AL-Read-Key, acessada pela estação através do túnel18761. Existe suporte Linux/systemd aos providers protegidos no HostIntegracao.

## Abordagem

Um serviço dedicado fugates-mm-purchase-orders.service, com runtime versionado próprio e modo de inicialização MM explícito. Reusar o host.NET10 e os componentes MM; extrair somente a coordenação/estado MM necessários de WinForms para código portátil compartilhado. O modo MM deve ser exclusivo e não registrar trabalhadores ACM, SD, Toledo ou Transportadoras.

Alternativas consideradas: manter tarefa Windows não atende à máquina desligada; embutir o MM no serviço ACM/Toledo acoplaria manutenção e reinício a fluxos que devem ser preservados. A unidade dedicada é o caminho adequado ao requisito do operador.

## Motor e identidade

- SAP e Guardian com os mesmos adapters e regras; nenhum novo motor empresarial ou novo mapeamento.
- PostgreSQL direto em127.0.0.1:5433 na VM; validar fisicamente cluster18/fugatesacmdev/banco/role/session_user antes de operar, como no registro atual.
- Identidade PostgreSQL permanece fugates_mm_app; nenhum uso de postgres/owner/role antiga como identidade operacional.
- SAP/Guardian/PostgreSQL carregados de providers legítimos existentes e vinculados de forma protegida ao novo serviço, preferencialmente systemd LoadCredential. Não colocar segredo em argumento, Git, pacote, log ou snapshot.
- A classe Linux de credenciais exige hoje0400 e restringe0440 à sonda SAP do serviço Toledo. Qualquer suporte0440 MM será limitado ao diretório real do novo serviço, CREDENTIALS_DIRECTORY correspondente, IDs aprovados, diretórios privados e ausência de links. Não relaxar a guarda global.
- Conferir os bindings por metadados; se faltar material legítimo na VM, preparar transferência específica protegida do provider aprovado. Não rotacionar credenciais ou usar a role comprometida.
- User Linux sem privilégio administrativo, seguindo a identidade corporativa já usada pelos workers. UMask0077, diretório de estado privado e hardening systemd compatível com rede e credenciais.
- enable no boot e Restart=on-failure com intervalo limitado; parada com drenagem do envio ativo e prazo suficiente ao fechamento durável. Não repetir POST após timeout, queda ou resultado desconhecido.

## Recorte de criação e intervalo aprovados

- Intervalo de consulta: 3 minutos, configurado uma vez e aplicado ao agendamento, à próxima atualização exibida e ao critério de atualidade. Não manter um limite de atualidade fixo menor que o intervalo, pois isso invalidaria a admissão legítima entre consultas.
- Corte fixo e inclusivo: 01/11/2026, pela data de criação do pedido no SAP. Não usar a data do pedido, a data de aprovação, a data da coleta ou a hora local da VM como substitutas sem comprovação contratual. O campo técnico de criação da API MM deve ser confirmado no contrato/metadata antes de compor o filtro; não copiar a sintaxe OData V2 do SD para a API OData V4 de compras.
- O corte não avança automaticamente na virada do dia nem no reinício: um pedido criado a partir de 01/11 que seja aprovado posteriormente continua elegível. Esse é o significado adotado para “a partir de hoje”, apresentado ao operador nesta revisão.
- Aplicar filtro SAP e revalidar a data de cada registro antes de reservar ou enviar. Data ausente, inválida ou anterior ao corte bloqueia a admissão; nunca ampliar silenciosamente para todo o acervo.
- Preservar os registros e protocolos anteriores para acompanhamento. Eles não são removidos, reabertos ou reenviados pela nova política, e não passam a ser elegíveis porque foram consultados depois do corte.
- SD e MM mantêm serviços, configurações, registros e regras separados. Reaproveitar padrões compatíveis, sem alterar a frente SD do Claude. Coordenar somente arquivos compartilhados e a publicação.

## Um único iniciador

Adicionar exclusividade do serviço por sessão PostgreSQL/advisory lock, sem nova tabela. Perda dessa exclusividade bloqueia nova admissão. Preservar a reserva única por empresa/pedido/item e o fechamento por UUID/estado, que continuam protegendo cada operação mesmo em falhas.

Na troca: desabilitar tarefa MM Windows e perfil de envio local; impedir que abrir a tela recrie um motor local. Verificar trabalho ativo antes da parada e readback durável depois. Se surgir resultado indeterminado, não repetir nem ocultar; interromper a ativação e reconciliar. Ativar o serviço somente após provar ausência do iniciador antigo. O servidor deve carregar o registro existente e pular todas as chaves já ocupadas, incluindo as161 recusadas.

## Monitoramento sem nova migration

Reutilizar o snapshot de observação MM como arquivo privado na VM, escrito atomicamente pelo único motor. O snapshot não é journal: resultados Guardian continuam persistidos e relidos no PostgreSQL; dados SAP e timestamps são identificados como observação da última consulta completa. Em erro, conservar a última leitura e marcar sua idade/indisponibilidade.

Adicionar exclusivamente GET /dev/read/mm na API privada18762, com a autenticação atual e a identidade física READ ONLY já validadas. O recurso lê o snapshot MM configurado, valida origem/cliente/empresa/centros/schema/modoVM/host, limites de tamanho, links e permissões; não recebe comandos. Registrar no envelope a proveniência do snapshot e os tempos de coleta/readback. Não apresentar cópia antiga como consulta atual nem afirmar que o arquivo é persistência empresarial.

Usar o usuário Linux existente apropriado ao worker/leitor ou uma permissão pontual de leitura do arquivo, sem tornar o estado acessível a outros usuários. Não criar porta pública ou alterar Nginx/firewall/DNS. Não há necessidade prevista de tabela, migration ou GRANT PostgreSQL novo.

O WinForms usa o mesmo túnel/chave privada de leitura do perfil unificado. A tela MM consulta a API, mostra motor na VM, conexão, última atividade/leitura, pedido/item, JSON SAP, JSON Guardian, HTTP e protocolo. A interface observa: não faz POST Guardian e não inicia worker local. Indisponibilidade do servidor/API é exibida; nunca voltar silenciosamente ao envio local.

## Escopo de arquivos previsto

- Aplicacao/Modelos/Mm e Servicos/Mm: estado/coordenação portáveis, mantendo os contratos de negócio.
- HostIntegracao/Mm e ponto de entrada do host: composição Linux e hosted worker exclusivo.
- CarrierSystemdCredentials e provider Guardian: adaptação estritamente delimitada para reutilização sem bootstrap de outro banco/fluxo.
- WinForms/Mm, FrmPedidosCompraMm: modo observador remoto e bloqueio da inicialização local.
- Callback.Http/DevUnifiedReadApi: recurso GET MM isolado e leitor de snapshot; demais recursos preservados.
- deploy/mm-linux: unidade, perfil sem segredo, instalação, preflight, backup e rollback candidatos.
- Testes focais de coordenação, credenciais, exclusividade, monitor/API e regressões compartilhadas.

## Validação e publicação

Offline: corte inclusivo de criação em 01/11/2026 (incluindo pedido aprovado depois), rejeição de data ausente/inválida/anterior, preservação do corte após virada do dia/reinício, intervalo de 3 minutos e atualidade coerente, filtro OData V4 comprovado, portabilidadeLinux, mesmos payloads, não repetir5sucessos/161recusas, chave concorrente, perda de lock, parada durante envio, armazenamento indisponível, credencial inválida/modo/link, API sem chave/sem arquivo/stale/escopo errado, ausência de fallback local e compatibilidade dos botões JSON. Nenhum POST empresarial de teste.

Preparar candidato versionado com hashes, startup inerte e plano de instalação. Antes da promoção: congelar publicação com Claude, verificar checksums, backup DEV110/API/serviço e confirmar os registros existentes por READ ONLY. Apresentar ao operador o pacote e o escopo operacional específico antes de instalar serviço, provisionar bindings ou reiniciar API/ativar VM.

Cutover: serviço preparado inerte, trabalhador Windows desabilitado/parado e readback sem ambiguidade; publicar cliente remoto e API; iniciar único serviço MM autorizado e verificar consulta/estado reais. Não usar contratos ou pedidos históricos como POST de teste. Pedidos futuros seguem a autorização empresarial vigente; os registros recusados dependem de recuperação separada, não de reinício do serviço.

Rollback: bloquear novas admissões e drenar/parar o serviço VM; preservar todos os registros, inclusive novos resultados/indeterminados; restaurar API/cliente/configuração e somente então reativar um iniciador Windows se expressamente escolhido. Nunca restaurar backup antigo sobre evidência de efeitos remotos nem deixar VM e Windows enviando juntos.

## Limites

Nenhuma alteração de regra MM além do intervalo e corte de criação aprovados nesta revisão; nenhuma criação de emissores/materiais, retry dos161, POST SAP, mudança deSD/ACM/Toledo,5432/5434,push/merge ou commit sem autorização. O serviço MM atual Windows permanece ativo durante preparação. A instalação na VM será a etapa operacional posterior ao candidato revisado.
