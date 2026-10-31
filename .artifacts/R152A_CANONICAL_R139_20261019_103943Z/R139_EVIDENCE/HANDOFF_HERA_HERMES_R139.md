# R139 — candidato SingleCreate seguro, exclusivamente offline

Gate: FUGATES-MASTERDATA-R139-SINGLECREATE-SAFE-STAGING-CANDIDATE.
Data: 18/10/2026. Escopo funcional: um CREATE da transportadora BP/código 20000014.

**Candidato pronto para QA de fontes e comportamento offline. Não é uma release publicada ou instalação autorizada. Não está pronto para CREATE real.**

```text
R139_STAGING_CANDIDATE=READY
R139_CURRENT_COMPOSITION=RECONCILED
R139_JOURNAL_TRANSACTION=IMPLEMENTED_IN_STAGING
R139_COMMIT_BEFORE_HTTP_OFFLINE=PASS
R139_DESKTOP_BINDING=READY
R139_ALTERNATE_CREATE_PROTECTION=PASS
R139_HML_IDENTITY_COMPATIBILITY=BLOCKED
R139_BUILD=PASS
R139_OFFLINE_TESTS=113_PASS_0_FAIL_0_SKIP_SELECTED
PHYSICAL_COMMIT_PROVEN=NO
DATABASE_CONNECTIONS=0
DATABASE_WRITES=0
EXTERNAL_POSTS=0
INSTALLED_RUNTIME_CHANGES=0
PUBLISH=0
RUNTIME_ENABLE=0
WINFORMS_STARTED=0
SOURCE_OFFICIAL_CHANGES=0
ACM_SOURCE_CHANGES=0
COMMIT=0
PUSH=0
NEXT_HANDOFF=HERA_V3_AND_HERMES_V3
```

READY refere-se à composição candidata compilada e testada offline. PASS de proteção alternativa refere-se aos entrypoints desta candidata, não à inexistência física de outros escritores instalados.

## 1. Procedência e isolamento

O usuário autorizou expressamente a cópia e o QA em:
`C:\Users\TI\Documents\ChatGPT\FugaTES\staging\R139_SINGLECREATE_SAFE`.

Baseline copiada de `C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_STAGING_MASTERDATA_P0`, preservando as alterações locais existentes. Não foi usado checkout histórico R73. Os 298 arquivos de baseline copiados tiveram seus hashes de origem reconferidos, sem alteração no staging original. A cópia excluiu bin/obj, Git, configurações privadas, saídas privadas e dados operacionais.

Foram validados:

- R135B ZIP: `E53D603855BA3DDACD2616DE1D13837FED8956E69A915670A5A0820A5F147907`.
- Relatório R135D: `8C4D5D2A00FDBDC94F1CECB88DF19108FF4E5A8FF763072DD52C2E78FD3E6655`.
- R135B já comprovou igualdade de quatro C# de referência R132 com a baseline atual. R139 altera deliberadamente binding/journal; use case e policy R31 permanecem nos bytes originais.

`CURRENT_BASELINE_PROVENANCE.json` registra os dois desvios anteriores perante os PDBs adjacentes R114: **CarrierDesktopLifecycle.cs** e **FrmCadastrosGuardianR3.cs**, com hash esperado no PDB e hash dos fontes atuais. São adições atuais R122/R123, preservadas na baseline e depois integradas pelo R139. Não se afirma que os binários R114 contenham essas adições, nem que exista equivalência com uma release instalada. A candidata foi compilada a partir da cópia atual.

O pacote contém 305 arquivos de fonte/projeto/recursos e configuração exclusivamente de build: fechamento de dependências da candidata, sem binários publicados ou repositório Git. São **8 fontes existentes modificados, 3 fontes produtivos novos, 2 arquivos de testes novos, 1 projeto de testes novo e 1 configuração NuGet offline de build**. Matriz detalhada: `SOURCE_CHANGE_MATRIX.json`. As oito versões anteriores acompanham `baseline-changed-files`.

## 2. Barreira transacional

`SingleCreateJournalPostgreSqlR31.TryPrepareAsync` agora:

1. Valida a mesma identidade/attempt/fingerprint pela policy R31.
2. Abre conexão no DataSource fornecido e inicia transação própria ReadCommitted.
3. Executa o INSERT PREPARED existente, parametrizado, na conexão e transação explícitas.
4. Para reserva recusada, aguarda rollback e retorna false. Contagem indeterminada gera exceção.
5. Para uma reserva, **aguarda CommitAsync e só então retorna true**.
6. Exceções de INSERT/COMMIT, cancelamento ou perda de confirmação propagam; o R31 retorna INDETERMINATE e não chama o writer. Dispose/rollback encerram os recursos. Não há retry.

`JOURNAL_SQL_INVARIANTS.json` demonstra que PrepareSql, ConfirmSql e ObserveSql não mudaram. Não houve migration, alteração de constraints, fingerprint, attempt_id ou regras de unicidade.

Para testar o código produtivo sem conectar PostgreSQL, o argumento do journal usa o contrato padrão `DbDataSource`, aceitando o mesmo NpgsqlDataSource produtivo. O helper de parâmetros usa CreateParameter com os mesmos nomes/valores; a inferência Npgsql é preservada. O comportamento de ConfirmAsync posterior ao HTTP é mantido. Isso exige recompilar consumidores; **não é atualização por substituição de DLL isolada**.

## 3. DataSource exclusivo e identidade HML

`DesktopRuntime.CreateSingleCreateSource` cria uma instância/pool própria a partir do perfil Desktop já validado. `SingleCreateDatabaseProfileR139.Options`:

- exige aprovação HML explícita, referência, host, porta, database, role e schema;
- exige correspondência exata desses campos com a conexão Desktop existente;
- rejeita os endpoints ACM conhecidos 5432/5433 e os nomes legados DEV, como proteção conservadora contra reaproveitamento do perfil antigo;
- configura **Enlist=false somente na nova fonte**, application name SingleCreate e search_path fugates;
- não redireciona host/credencial, não troca usuário, não modifica o pool Carrier genérico e não registra connection strings.

BindAsync também rejeita uma fonte com Enlist habilitado. O preflight do journal não é usado como prova de COMMIT da reserva.

O perfil candidato precisa das chaves **novas e ainda não preenchidas** `Carrier:SingleCreate:PostgreSql:{Environment,Approved,EvidenceReference,Host,Port,Database,Role,Schema}`. Precisa ainda de `SpecificCreateAuthorized` e `ExecutionApprovalReference` sob `Carrier:SingleCreate`. As demais aprovações continuam vindo dos contratos existentes. Isso é catálogo de configuração de código, não um perfil homologado.

O Desktop antigo continua exigindo `fugates_dev/fugates_dev_app` em **CreateCarrierStore**, usado pelo fluxo genérico. O caminho unitário R139 não chama essa factory nem depende das seis carrier_sync_*. Contudo o bootstrap Desktop deve ser configurado externamente, mediante autorização, para a identidade MasterData HML segregada e legitimamente comprovada. **Não há perfil efetivo criado neste pacote. A existência do cluster 5434 não foi presumida ou consultada.**

Identidade física, constraints completas, ACLs, migration canônica R137, fsync/synchronous_commit, confirmação e recuperação de COMMIT e exclusão de outros escritores são **PENDING_PHYSICAL_VALIDATION**. R137 é aprovado offline segundo o pedido; seus SQLs não foram alterados, aplicados ou promovidos por este gate.

## 4. Caminho Desktop e guardas

Composição candidata:

`CarrierDesktopComposition.Create` → lifecycle existente + `SingleCreateDesktopControllerR139` → formulário existente.

O controle **VALIDAR CREATE (SEM POST)** é explícito e permanece desabilitado sem os pré-requisitos. Após liberação operacional futura, verifica a identidade física e o journal por consultas read-only, sem SAP/Guardian ou INSERT. Somente sucesso real cria a sessão dona do DataSource; nenhuma flag em memória substitui esse preflight.

O botão **CRIAR NO GUARDIAN (UNITARIO)** continua desabilitado por padrão. Quando todos os controles efetivos forem satisfeitos:

`handler → controller → Masterdata.ExecutarCriacaoUnitariaAsync (mesmo lock/lifetime) → preparação R41 → BindAsync R41 → UseCase R31 → journal R31 → writer existente`.

A preparação reutiliza SAP reader R20B, CRM010/R25T, codec R37, aprovação funcional R41 de BP/código 20000014 e estado=1. Não há fallback de BP nem cópia de dados SAP para snapshots/logs. Os valores pessoais ficam na operação em memória, com descarte do corpo preparado.

R31 conserva a consulta escopada de existência, aprovação específica/temporal, assinatura HML, callback aprovado, payload completo, journal e HTTP200/código correto. O R123C antigo continua apenas diagnóstico quando não há controller; não é fallback de POST.

O controller consome uma única sessão/tentativa; nova tentativa exige novo preflight e continua sujeita à unicidade e reconciliação no journal. O resultado INDETERMINATE preserva o attempt e o aviso **RECONCILIACAO OBRIGATORIA; SEM RETRY AUTOMATICO** na UI, inclusive depois do refresh.

## 5. Revogação e encerramento

Uma lease guarda o snapshot exato de configuração atestado, em memória. Recarrega o perfil antes de avaliar prontidão, antes de dispatch e na guarda HTTP. Perfil removido, ilegível, revogado ou com identidade/configuração divergente bloqueia; não há rebind silencioso. O teste após PREPARED confirma zero writer mesmo depois de COMMIT reconhecido, deixando a reserva para reconciliação.

O fechamento usa o cancelamento/lock já existentes; aguarda o encerramento das operações antes de descartar a sessão e o DataSource. Não se cria timer, worker ou serviço para SingleCreate. Prévia, consultas manuais e automático não recebem autorização de escrita apenas porque o unitário foi autorizado: há callback de autorização específico no mesmo orquestrador.

## 6. Rotas alternativas

| Caminho | Situação candidata |
|---|---|
| Botão unitário R139 | R31, lock compartilhado, journal, fonte exclusiva, todos os gates |
| R123C sem controller | Diagnóstico, nenhum writer |
| SincronizarTransportadoras via Desktop | Start/poll e StartSession bloqueados em modo unitário |
| SincronizarTransportadoras via Host | Registro genérico bloqueado antes da configuração Linux/worker quando modo unitário ativo |
| Adapter Guardian direto | Continua infraestrutura com guardas; não é acionamento da UI nem alternativa ao journal |

Os flags gerais/INC005 e Carrier automático devem estar explicitamente falsos. Nenhuma alteração indiscriminada foi feita no algoritmo genérico de sincronização. Não foi atestada ausência de processos de outras releases; isso é requisito físico posterior, antes do primeiro ensaio.

## 7. Build, regressões e revisão

Build focal real: `dotnet build src/FugaTES.WinForms/FugaTES.WinForms.csproj --no-restore -c Release`.
Exit code 0, **zero avisos e zero erros**. O restore usou fonte local/cache, sem fonte HTTP. O SDK precisou de permissão de leitura de configuração fora do sandbox; não foram copiados ou exibidos seus valores privados.

Resultados finais verificados no TRX:

- `final-R139.trx`: **31/31**, testes novos de transação, falhas, commit aguardado, replay, concorrência simulada, fonte/enlistment, controller/lock, revogação, estado UI e rotas bloqueadas.
- `final-R105-regression.trx`: **74/74**, classes focais de journal, binding, prontidão e pre-dispatch.
- `final-R41-regression.trx`: **8/8**, seleção dos gates/resultado afetados pela integração.

**113 selecionados passaram; zero falhas ou skips nos três resultados finais.** Não são 113 testes históricos repetidos indiscriminadamente. Os doubles exercitam a classe journal e o caso de uso reais, sem SQL ou HTTP externo; não comprovam ACID ou constraints físicas.

Três casos antigos ficaram fora da seleção: dois fazem TCP com PostgreSQL indisponível (incompatível com a proibição de conexão); um exige JSON privado R117, que não foi exportado. Um primeiro run de regressão teve 71 aprovações e quatro erros de recursos: três foram resolvidos copiando o marcador original da solução; o quarto corresponde à fixture privada excluída. Logs/TRX dessa execução e dos REDs foram preservados, sem ocultar o histórico.

Revisão final independente de código encontrou: revogação não refletida, resultado UI sobrescrito e encoding. Os três foram corrigidos e conferidos; sem achado remanescente nesse escopo. Não houve QA visual ou startup WinForms. Detalhes em `REVIEW_DISPOSITION.md` e `qa/progress.md`.

## 8. Próxima ação

Hera/Hermes devem revisar o delta candidato e a política configurável de identidade. Atlas/Gaia precisam comprovar o destino segregado HML e fornecer o perfil/migration canônica para o preflight físico autorizado. Depois serão necessárias autorizações separadas para instalação, validação física de persistência e primeiro CREATE.

**FIRST_REAL_BLOCKER=MASTERDATA_HML_PHYSICAL_IDENTITY_AND_JOURNAL_NOT_ATTESTED.**

Não instalar, iniciar, habilitar ou fazer POST com base neste pacote. Ele não contém perfil operacional, credenciais ou aprovação de execução real.
