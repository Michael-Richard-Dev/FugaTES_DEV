# Matriz funcional R139 versus R150C

Comparação dos bytes e código dos dois ZIPs identificados em PACKAGE_IDENTITIES.json. PASS de comportamento abaixo é conclusão estática do caminho, não teste reexecutado nem prova PostgreSQL física.

| Capacidade | R139 aprovado | R150C entregue | Conclusão |
|---|---|---|---|
| BeginTransactionAsync | Já existe no adapter; READ COMMITTED; command.Transaction ligado | Existe; READ COMMITTED; NpgsqlCommand recebe a transação | Não foi introduzida pelo R150C |
| COMMIT antes de retornar Reserved/true | await transaction.CommitAsync(ct), seguido de return true | Mesmo ordenamento | Preservado no adapter |
| Falha/indeterminação de COMMIT sem HTTP | Exceção propaga ao use case; nenhuma chamada writer após catch | Falha de COMMIT também propaga; use case idêntico | Preservado no código comum; nenhuma durabilidade real testada aqui |
| Linhas afetadas =0 | RollbackAsync aguardado; false se rollback concluído | RollbackSilenciosoAsync; false | Despacho bloqueado em ambos; tratamento de falha do rollback mudou |
| Linhas afetadas desconhecidas (-1/2 etc.) | ClassifyInsert=Unknown; rollback; lança SINGLE_CREATE_PREPARE_ROWS_UNKNOWN; use case INDETERMINATE | Todo resultado !=Reserved retorna false após rollback silencioso; use case BLOCKED/PENDING_OR_REPLAY | Sem POST, mas Unknown foi reclassificado como rejeição: delta semântico não equivalente |
| Rollback com falha | Exceção preservada para tratamento conservador do use case | catch vazio no helper de rollback; erro pode ser suprimido | Perda de sinal diagnóstico; revisão focal obrigatória |
| SQLSTATE23505/23P01 | Classificado como Rejected por política; descarte assíncrono da transação ao sair do try | Catch apenas da execução do INSERT, com rollback explícito silencioso | Novo escopo de catch, não nova política; não equivale textualmente ao R139 |
| Policy e estados | SingleCreateJournalPolicyR31 | Mesmos bytes e SHA-256 | Idêntico; discrepância de Unknown está no uso da policy pelo adapter |
| ADO.NET seam de testes | Adapter aceita DbDataSource/DbCommand, possibilitando testes reais de ordenamento e falha offline | Adapter aceita NpgsqlDataSource/NpgsqlCommand | Mudou API/seam; resultados antigos não validam automaticamente o adapter reescrito |
| Enlist=false no binding | BindAsync rejeita source.Enlist=true antes de preflight | Guarda removida do BindAsync | Proteção de fronteira não preservada |
| Enlist=false na factory | ProfileR139.Options fixa false, sem redirecionar identidade/credencial | MasterdataHmlDataSourceR147B.Normalizar fixa false; Validar rejeita true | Proteção local no helper existe; não prova uso pelo botão final |
| Datasource físico MasterData | Perfil explícito HML, fonte própria, preflight schema/role/journal/índices/checks | Novo helper15434, databasefugates_masterdata_hml, rolefugates_masterdata_hml_app; probecluster18/fugatesmdhml eporta efetiva5434 | Melhora componente de identidade física, mas está desconectado da UI |
| Rejeição5432/5433 | Perfil rejeita portas5432/5433 e identidadefugates_dev; composição unitária separada | Helper rejeita5432/5433/15432/15433; probe rejeita cluster/porta remota divergente | Componente presente; Desktop final não comprovado |
| Assertions físicas do journal | Schema atualfugates, app semCREATE/super/createdb/createrole, índices únicos válidos e4CHECKs | Probe novo verifica db/cluster/porta/current_user/PG18/recovery; Bind só existência/tabela ACL | Não há paridade completa do preflight R139; não foi feita nova inspeção física |
| Botão e controlador | _create.Click -> AcionarSingleCreateAsync -> lifecycle.SingleCreate.ExecuteAsync -> sessãoR139 -> BindAsync -> use case | Botão -> AcionarSingleCreateAsync -> VerificarAsyncR123C -> texto "aguardando composição" | Regressão de integração funcional comprovada |
| Preflight UI semPOST | Botão VALIDAR CREATE chama controlador.ValidateAsync e usa lock compartilhado | Não presente na Form entregue | Não preservado |
| Fonte e composição unitária |3novos arquivosR139, factory no lifecycle, chamada a CreateSingleCreateSource | Os3arquivos não estão no R150C; factory/lifecycle/Form são iguais ao baseline anterior ao R139 | R150C não é superconjunto do R139 |
| Lock e autorização independente | Controlador usa orquestrador real; singleCreateAutorizado permite unidade sem Carrier ativo | Lock básico permanece, mas delegate específico foi removido e controlador ausente | Não é equivalente ao wiring R139 |
| CarrierSyncR3/tabelascarrier_sync_* | Composição unitária usa journalR31; StartAsync evita poll em unitary mode | Composição HML nova também usa apenas journalR31; rota antiga Carrier geral ainda existe | NOT_REQUIRED para SingleCreate; não instalar R3 para suprir wiring ausente |

As fontes completas focais estão em sources/R139, sources/R150C e sources/R139_PREPATCH_BASELINE, com hashes em SOURCE_COMPARISON.json/csv e manifesto da evidência.
