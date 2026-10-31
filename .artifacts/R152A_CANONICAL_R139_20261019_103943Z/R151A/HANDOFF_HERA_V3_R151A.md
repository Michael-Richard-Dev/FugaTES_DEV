# Reconciliação de proveniência R139 e R150C

Conclusão: a divergência documental está resolvida pelo código. O R139 já implementava transação explícita e aguardava COMMIT antes de liberar a reserva. O R150C reescreve esse adapter e acrescenta um helper de composição HML, mas o ZIP entregue não preserva a composição Desktop aprovada no R139. Não recomendar o R150C como candidato final integrado.

## Identidade dos pacotes

R139: C:\Users\TI\Documents\ChatGPT\FugaTES\evidence\R139_SINGLECREATE_SAFE_20261018_132247Z\FUGATES_MASTERDATA_R139_SAFE_SINGLECREATE_CANDIDATE_20261018.zip
SHA-256: 3AD71CBEBED15DA760AC2C1C21DB9389390C77D3D68079B1CFFECB8738139262
R150C: C:\Users\TI\Downloads\FUGATES_MASTERDATA_R150C_RUNTIME_COMPOSITION_20261018_212942.zip
SHA-256: 20E118ED62117EFFFD79F159D7B6E49459698CD4642F67659F62E48BB117CBFB
Ambos correspondem exatamente aos hashes informados e passaram CRC ZIP. Caminho R139 reutilizado do R146C, sem recuperar/reconstruir fontes por relatório. Os fontes do R139 usados são os de candidate/, não os de baseline-changed-files/. Os últimos só servem para provar a origem anterior das variantes presentes no R150C.

## Proveniência transacional resolvida

SingleCreateJournalPostgreSqlR31.cs do R139 (SHA331BD60371D8DE1E864F22A7BD6B239B5CFB896A21AA4D114C4E364F0C3EAF6A) já faz OpenConnectionAsync, BeginTransactionAsync(ReadCommitted), liga command.Transaction, executa INSERT, aguarda RollbackAsync para zero/unknown e aguarda CommitAsync antes de true. Portanto, o comentário R150C "a reserva passa a ter TRANSACAO EXPLICITA" não descreve uma novidade em relação ao candidato R139 aprovado.

O adapterR150C (SHA12C507CF266B1977BCA0B9C3B2B9785C54D1A04F57A24B4A0D6C7FE1C094A4EB) mantém COMMIT aguardado, mas muda DbDataSource para NpgsqlDataSource, os comandos/parâmetros, o escopo do catch de SQLSTATE e a política efetiva de erro do rollback/linhas inesperadas. ClassifyInsert continua idêntico na policy, porém o adapterR150C transforma Unknown em false. O R139 lançava SINGLE_CREATE_PREPARE_ROWS_UNKNOWN, e o use case devolvia CREATE_PREPARE_OUTCOME_UNKNOWN/INDETERMINATE. No R150C passa a rejeição BLOCKED. Não há prova de POST antes de COMMIT; há uma diferença real de classificação conservadora que impede afirmar equivalência completa.

O use case GuardianSingleCreateUseCaseR31 e SingleCreateJournalPolicyR31 têm exatamente os mesmos bytes nos dois pacotes. TryPrepareAsync é aguardado; false ou exceção interrompem antes de writer.GravarAsync. Exceção de COMMIT não é convertida em sucesso pelo R150C. Essa é a base de R151A_COMMIT_BEFORE_HTTP=PRESERVED, limitada ao código, sem ensaio ACID físico.

Enlist: o R139 fixa Enlist=false na sua fonte e também o exige em BindAsync. No R150C, a factory nova fixa false, mas a verificação de BindAsync foi removida. Não se pode atribuir a nova factory a uma chamada que ainda não existe no Desktop. TransactionScope/Transaction.Current foram encontrados apenas em testeR139, não no código de produção fornecido; isso não dispensa a barreira contra enlistment de chamadores externos.

## Caminho real do Desktop

Comum aos dois: Program.Main -> CarrierDesktopComposition.Create -> PainelInicialForm -> FrmIntegracoes -> FrmCadastrosGuardianR3.

R139: CarrierDesktopComposition injeta singleCreateFactory; CarrierDesktopLifecycle expõe SingleCreate e autorização unitária própria; Form disponibiliza VALIDAR CREATE e, no botão de execução, chama lifecycle.SingleCreate.ExecuteAsync. O controlador usa o lock real e a sessão prepara datasource dedicado, prova identidade/journal e chama BindAsync/use case. A sessão também preserva escopo BP20000014, lease do perfil, autorização específica e guard HTTP por método/rota. Não exige criar/ativar CarrierSyncR3 para o journal unitário.

R150C: o handler AcionarSingleCreateAsync apenas chama SingleCreateDispatchContractR123C.VerificarAsync e exibe bloqueio/"aguardando composição"; não chama o controlador ou use case. Não constam os arquivos SingleCreateDesktopControllerR139.cs, SingleCreateDesktopCompositionR139.cs ou SingleCreateDatabaseProfileR139.cs. Form, DesktopRuntime, CarrierDesktopComposition, CarrierDesktopLifecycle, orquestrador e binding são byte a byte iguais às respectivas fontes pré-patch existentes dentro do ZIPR139 (seis correspondências). Isso é evidência material de que o wiring R139 não foi incorporado, não inferência baseada no nome de release.

O método MasterdataHmlSingleCreateCompositionR150C.ComporAsync só tem sua declaração em src/; suas chamadas identificadas estão em testes. A chamada BindAsync existente dentro dele não torna a classe alcançável pela tela. O csproj WinForms é o SDK padrão, sem entrada alternativa comprovada que acione esse helper. CALL_REFERENCES.json e o diff registram os pontos.

## Datasource HML e preservação das guardas

Os helpers R147B/R150C têm código útil: fonte própria, Enlist=false, rejeição das portas locais5432/5433/15432/15433 e comparação da porta efetiva do servidor5434, cluster18/fugatesmdhml, banco/role próprios, PG18 e fora de recovery. O probe é um SELECT e não altera banco. Contudo, a UI entregue não o utiliza. Assim R151A_HML_DATASOURCE=BLOCKED significa vínculo Desktop não comprovado; não significa que os testes locais do helper falharam.

O preflight novo não reproduz todas as barreiras R139: schema atual/ausência deCREATE e flags administrativas da role/índices únicos válidos deattempt,bp,guardian_code/CHECKs do journal. JournalReadyAsync verifica tabela/ACLs, sem substituir essas assertions. A base R149A fechada é respeitada; este gate não abre banco nem repete sua homologação. Apenas registra a perda de paridade de código, a ser considerada na integração focal.

CarrierSyncR3 continua NOT_REQUIRED para o contrato unitário e para os helpers dedicados. O bootstrap Carrier genérico remanescente no R150C verifica carrier_sync_* para outro fluxo; não promover isso a requisito do SingleCreate. A integração deve aproveitar os componentes unitários existentes e não instalar dependência artificial para contornar o wiring ausente.

## Escopo mínimo de revisão posterior

Preservar R139 como referência da implementação transacional e do controlador/callpath. Aproveitar o datasource físico segregado existente no R150C em uma integração focal autorizada, sem refazer SingleCreate e sem trocar DLLs individualmente. Antes de aceitar um novo candidato integrado:
1. Conciliar somente o delta do adapter: linhas0/1/desconhecidas, rollback que falha, SQLSTATE23505/23P01, cancelamento/falha deCOMMIT e ausência deHTTP após resultado desconhecido. Preservar a classificação conservadora do R139 ou submeter a mudança à revisão independente; não chamar isso retroativamente de função nova do R139.
2. Preservar as barreirasEnlist e preflight e conectar os componentes de Desktop já existentes ao datasource dedicado, mantendo lock, aprovação por perfil e cancelamento. Não habilitar SingleCreate durante essa integração.
3. QA focal do botão -> controlador -> composiçãoHML -> binding -> journal -> COMMIT -> gateway com doubles e perfil inválido, sem repetir275testes ou suites históricas inteiras. Esta recomendação não autoriza implementar ou executar tais testes neste gate.

R151A_REQUIRED_QA_SCOPE=FOCAL_TRANSACTION_DELTA. O termo inclui a verificação focal de integração necessária para comprovar que o adapter revisado é realmente o usado pelo botão, não uma auditoria geral ou reexecução de suítes históricas.

## Observação de manifesto

O arquivo histórico .evidence-p0/MANIFEST_SHA256_DELTA.txt no R150C lista journal4323bytes/hash7B67... (baseline anterior), enquanto o journal atual tem5684bytes/hash12C507.... Ele não foi utilizado como inventário atual. PROVENANCE_DIAGNOSTICS.json registra ambos. A identidade do ZIPR150C está correta; este inventário focal foi recalculado dos bytes reais e não atribui hash histórico ao conteúdo novo.

Os275/275testes e buildR150C são base informada; não foram reexecutados nem usados para substituir prova de callpath. Nenhum build, teste, HTTP, conexãoSQL, alteração de fonte/pacote original/runtime, habilitação ou mudançaACM foi realizada. Os .cs incluídos são cópias byte a byte dos membros dos ZIPs, para evidência, não fontes reconstruídos ou candidato corrigido.

## Retorno

R151A_R139_PACKAGE=VERIFIED
R151A_R150C_PACKAGE=VERIFIED
R151A_TRANSACTION_PROVENANCE=RECONCILED
R151A_COMMIT_BEFORE_HTTP=PRESERVED
R151A_DESKTOP_CALLPATH=BLOCKED
R151A_HML_DATASOURCE=BLOCKED
R151A_CARRIER_SYNC=NOT_REQUIRED
R151A_FINAL_CANDIDATE=BLOCKED
R151A_REQUIRED_QA_SCOPE=FOCAL_TRANSACTION_DELTA
DATABASE_CONNECTIONS=0
SOURCE_CHANGES=0
BUILD_EXECUTED=0
TESTS_REEXECUTED=0
EXTERNAL_POSTS=0
NEXT_HANDOFF=HERA_V3
