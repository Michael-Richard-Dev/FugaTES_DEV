# Handoff HERA V2 — R15
GATE=FUGATES-MASTERDATA-CARRIER-MANUAL-READONLY-R15-CODEX
Data da conclusão das verificações: 11/10/2026, aproximadamente 18:01 Brasília.
Modo: LOCAL_WINDOWS_IMPLEMENTATION / OFFLINE_BEHAVIORAL_QA.

IMPLEMENTATION=PASS (implementação e comportamento offline; não é homologação real HML)
MANUAL_ENTRYPOINT=IMPLEMENTED
MANUAL_DELTA_INDEPENDENT=SIM
AUTOMATIC_PIPELINE_UNCHANGED=SIM (core automático, discovery, checkpoint e writes preservados)
SHARED_LOCK=PASS
MANUAL_ENQUEUE_DML=0
BUILD=PASS (exit 0; zero avisos; zero erros)
BEHAVIORAL_TESTS=71 PASS / 0 FAIL / 0 SKIP
R13_PATCH_PRESERVED=SIM
STAGING_R3_2_UNCHANGED=SIM
EXTERNAL_WRITES=0
FIRST_BLOCKER=COMPLETE_COMPARISON_NOT_PROVEN_FOR_REAL_NO_ACTION
NEXT_HANDOFF=HERA V2

## Implementação entregue
Novo caso de uso ConsultarTransportadoraManualReadOnlyCasoDeUso, com portas exclusivamente de leitura e resultados em memória. Valida um único BP string e preserva zeros à esquerda. Não chama EnfileirarPedidoAsync, discovery incremental, worker ou store.

O adapter SAP reutiliza ClienteHttpParceiroNegocioSap e autenticação existente. O GET individual A_BusinessPartner expande to_BusinessPartnerRole; a role CRM010 é obtida nesse mesmo GET, sem contrato delta ou filtro de centro. Parser OData V2 existente é reutilizado com validação adicional do offset. Datas ausentes/inválidas, navegação incompleta e fronteiras temporais ambíguas ficam INDETERMINATE. Semântica instantânea comprovada usa limites inclusivos; quando não comprovada, a decisão de atividade só avança se invariável entre interpretações de instante e data civil admitidas, sem adotar fuso brasileiro ou horário final inventado.

O adapter Guardian exige ProfileVerified, SignatureVerified, evidência e vínculo BP/código específico comprovado. O idAssinatura do GET é separado da assinatura do CreateToken. Não deduz codigo a partir de BP ou sequencial. GET 404 só representa ausência no escopo aprovado. Coleções ambíguas, identidade divergente e respostas inválidas não produzem sucesso fictício.

Comparação ordinal exata dos campos aprovados. A composição real possui somente o mapeamento documental BusinessPartnerFullName -> razaoSocial e marca equivalência completa como falsa. Divergência comprovada produz UPDATE_CANDIDATE; igualdade parcial produz INDETERMINATE. NO_ACTION só passa no caso de uso quando o contrato de comparação completa está comprovado e todos os campos pertinentes estão presentes. Os testes utilizam um contrato completo sintético explícito; isso não promove a composição real a homologada. Nenhuma normalização de endereço/UF/documento foi introduzida.

O entrypoint CarrierDesktopLifecycle.ConsultarManualReadOnlyAsync utiliza o mesmo _exclusao do OrquestradorCompartilhadoMasterdataR3. Exclusão mútua foi executada com o pipeline automático simulado e com encerramento/cancelamento, sem DML. Não existe novo lock, worker ou timer para a consulta. O finally do bootstrap automático agora preserva esse lock aberto enquanto a aplicação estiver aberta, mesmo quando o delta estiver indisponível; o fechamento cancela e aguarda a consulta. O pipeline automático de negócio não foi modificado.

A tela existente recebeu a ação explícita CONSULTA MANUAL — SOMENTE LEITURA. O pedido persistente Sincronizar agora foi preservado. O botão depende do PostgreSQL runtime existente validado e do perfil de leitura aprovado. Nenhuma WinForms foi iniciada neste gate; não se declara QA visual executado.

O perfil reutiliza FugaTES.carrier.json e os providers/targets de credenciais existentes. Não altera ProfileVerified/SignatureVerified nem configuração real. Exige autorização de leitura específica, SAP HML client 110, BP/SID autorizados, prazo válido, timeout, cinco flags de escrita/worker/incremental explicitamente falsas e contrato Guardian comprovado. A nova guarda HTTP permite somente os GET aprovados e POST de autenticação CreateToken, nunca POST/PUT de negócio. Não foi feita chamada HTTP real nem leitura de segredos pelo executor dos testes.

## Arquivos criados/alterados nesta execução
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.Aplicacao\Transportadoras\CarrierManualReadOnly.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.Integracao.SAP\BusinessPartner\CarrierManualSapReadOnlyAdapter.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.Integracao.Guardian\GuardianCarrierManualReadOnlyAdapter.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.WinForms\Transportadoras\CarrierManualReadOnlyComposition.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.WinForms\Transportadoras\CarrierDesktopLifecycle.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.WinForms\Transportadoras\MasterdataR3\MasterdataSharedOrchestratorR3.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\src\FugaTES.WinForms\Transportadoras\MasterdataR3\FrmCadastrosGuardianR3.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\tests\FugaTES.Testes\CarrierManualReadOnlyR15Tests.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\tests\FugaTES.Testes\CarrierManualReadOnlyHttpR15Tests.cs
- C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\tests\FugaTES.Testes\CarrierManualReadOnlyProfileR15Tests.cs

Arquivos previamente modificados/untracked foram preservados; a lista acima não atribui ao R15 os demais deltas preexistentes.

## Execução verificável
Ciclo RED do caso de uso: 8 falhas / 6 passes antes da implementação. Após correção: 14 passes.
Ciclo RED do entrypoint/lock: 3 falhas / 14 passes. Após correção: 17 passes.
Primeira rodada dos três arquivos R15: 53 passes.
Rodada final: 55 casos R15 + 16 casos existentes do lifecycle/orquestrador = 71 passes.

Comando final de testes, cwd da worktree:
```text
dotnet test tests\FugaTES.Testes\FugaTES.Testes.csproj --configuration Release --artifacts-path .artifacts\manual-readonly-r15 --no-restore --filter "FullyQualifiedName~CarrierManualReadOnly|FullyQualifiedName~CarrierDesktopLifecycleTests|FullyQualifiedName~MasterdataWinFormsOrchestrationR3Tests" --logger "trx;LogFileName=manual-readonly-r15-final.trx" --results-directory .artifacts\manual-readonly-r15\evidence\tests --verbosity quiet
EXIT_CODE=0
```

Comando de build real:
```text
dotnet build FugaTES.slnx --configuration Release --artifacts-path .artifacts\manual-readonly-r15 --no-restore --verbosity quiet "-flp:logfile=.artifacts\manual-readonly-r15\evidence\build-release.log;verbosity=minimal"
EXIT_CODE=0
WARNINGS=0
ERRORS=0
```

### Onze famílias comportamentais exigidas
| Família | Evidência executada |
|---|---|
| MANUAL_BP_ONLY | BP único, batch rejeitado, zeros preservados, código Guardian distinto |
| CRM010_ELIGIBILITY | CRM010 ativa avança; vencida bloqueia antes do Guardian |
| CRM010_DATE_INDETERMINATE | Parser JSON/OData real; datas inválidas e fronteira ambígua |
| GUARDIAN_GET_200_404 | Adapters HTTP reais com handler fake: 200/404/403/500 e assinatura divergente |
| CANDIDATE_CREATE_NO_WRITE | 404 scoped gera candidato; somente GET de negócio no handler |
| CANDIDATE_UPDATE_NO_WRITE | Diferença aprovada gera candidato; somente GET de negócio no handler |
| NO_ACTION | Igualdade parcial indeterminada; completa comprovada gera NO_ACTION |
| PUT_COLLECTION_RESOLUTION | Adapter R13 real executado com PUT exclusivamente fake para collection |
| SHARED_LOCK | Bloqueio em ambas as direções, cancelamento e liberação após falha |
| WRITE_GUARD | Perfil não autorizado e métodos/rotas proibidos bloqueiam antes do dispatch |
| NO_BACKGROUND_WORKER | Consulta sem Start/factory/session; zero enqueue; bootstrap delta fake falha sem iniciar Carrier |

A regressão de 16 casos usa clock e sessões em memória; não inicia aplicativo operacional, serviço, worker Carrier real ou conexão. Não houve repetição dos 133 testes anteriores nem regressão integral.

## Evidências e preservação
- tests/manual-readonly-r15-final.trx: 71 executados/aprovados, 0 falhas, 0 skips.
- build-release.log: saída do build real.
- protected-before.json e protected-after.json: 50 arquivos protegidos, 50 hashes idênticos, 0 divergências.
- source-manifest.json: SHA256 dos dez arquivos R15.
- git-status-before.txt e git-status-after.txt: estado local sem commit/push.

O manifesto protegido inclui R13.1 GuardianCarrierSyncAdapter/CarrierProtocolBinding, core/discovery automático, Program/DesktopRuntime/DesktopTunnel, migration Carrier, fontes SAP SD, staging R3.2 e executável oficial.

SHA256 executável oficial preservado:
9E92340F768BB3437B942B295FA7AA48A4683E54D5D53CC976C393A745B3E7B6

SAP_HTTP_REAL=0
GUARDIAN_HTTP_REAL=0
SAP_WRITE=0
GUARDIAN_BUSINESS_WRITE=0
DATABASE_DDL=0
DATABASE_BUSINESS_DML=0
WINFORMS_STARTED=0
WORKERS_STARTED=0 (nenhum worker operacional; somente lifecycle/session fakes nos testes)
DESKTOP_OFFICIAL_WRITE=0
STAGING_R3_2_OVERWRITE=0
SAP_SD_SOURCE_CHANGE=0
COMMIT=0
PUSH=0
SECRET_OUTPUT=0

## Limite e próximo passo mínimo
A implementação offline está entregue. Nenhuma conexão HML ou validação visual foi executada. Para uso real, HERA deve comprovar o perfil autorizado de leitura, o vínculo BP/código e o escopo idAssinatura; as novas chaves de autorização não foram promovidas automaticamente. Para produzir NO_ACTION real, ainda falta contrato completo de equivalência dos campos pertinentes. Enquanto não houver prova, igualdade parcial permanece INDETERMINATE e nenhuma decisão habilita POST/PUT.
