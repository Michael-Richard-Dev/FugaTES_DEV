# Handoff canônico R139 — R152A

## Artefato original

O ZIP nesta pasta é cópia byte a byte do original usado na reconciliação R151A. Nenhum fonte foi reconstruído ou alterado. O hash esperado é `3AD71CBEBED15DA760AC2C1C21DB9389390C77D3D68079B1CFFECB8738139262`.

Use os fontes internos de `candidate/`. O diretório `baseline-changed-files/` contém versões anteriores para comparação e não deve ser usado como candidato aprovado.

`CANONICAL_FOCAL_SOURCES/` contém somente cópias byte a byte das entradas `candidate/` identificadas em `CANONICAL_FILE_MAP.json`; o ZIP original contém a baseline completa. Não substituir DLLs ou promover runtime neste handoff.

## Responsabilidades canônicas

- Reserva: SingleCreateJournalPostgreSqlR31.TryPrepareAsync abre transação (linha 43), faz rollback nas rejeições/resultado desconhecido e aguarda CommitAsync (linha 62) antes de retornar reserva confirmada.
- Resultado desconhecido: GuardianSingleCreateUseCaseR31 aguarda TryPrepareAsync (linha 57); exceção retorna CREATE_PREPARE_OUTCOME_UNKNOWN (linha 59), antes de writer.GravarAsync (linha 62).
- Enlistment: SingleCreateDatabaseProfileR139 define Enlist=false (linha 29); SingleCreateDesktopBindingR41 rejeita datasource com Enlist habilitado (linha 84).
- Composição: SingleCreateDesktopCompositionR139 cria datasource dedicado e chama BindAsync; DesktopRuntime.CreateSingleCreateSource é a factory.
- Controller: SingleCreateDesktopControllerR139.ValidateAsync e ExecuteAsync usam a autorização e exclusão compartilhadas.
- Botão: FrmCadastrosGuardianR3 registra _create.Click (linha 69) para AcionarSingleCreateAsync (linha 252), que utiliza o controller do lifecycle.

## Reconciliação R151A preservada

Consulte `R151A/HANDOFF_HERA_V3_R151A.md` e `R151A/CAPABILITY_COMPARISON_MATRIX.md`. R139 já continha a transação aprovada. R151A apontou lacunas no wiring/datasource do R150C; este handoff não corrige nem promove esse candidato. CarrierSync R3 não é dependência do SingleCreate no callpath comprovado.

## Confirmação do destinatário

A leitura e os hashes foram verificados pelo Codex localmente. Isso não comprova acesso do processo Claude Code. R152A_CLAUDE_FILE_ACCESS permanece BLOCKED até confirmação do destinatário no seu contexto real, identificando o caminho do ZIP e o SHA-256 lido. Não executar fonte, build, teste, banco ou HTTP para confirmar o recebimento.

## Limites

SOURCE_PATCH=0; DATABASE_CONNECTIONS=0; DATABASE_WRITES=0; BUILD=0; TESTS=0; DEPLOY=0; EXTERNAL_POSTS=0; ACM_CHANGES=0.
NEXT_HANDOFF=CLAUDE_CODE_AND_HERA_V3
