-- FugaTES Incremento 005 Gate 005-B
-- SQL documental PostgreSQL 18 para auditoria offline.
-- Desenho futuro de DI: NpgsqlDataSource = Singleton; connections curtas por operacao; repositories scoped/transient.
-- NAO EXECUTAR neste gate. PostgreSQL real, SKIP LOCKED real, constraints reais e concorrencia real pertencem ao 005-C.

create table integracao (
    id uuid primary key,
    origem text not null,
    destino text not null,
    operacao text not null,
    chave_negocio text not null,
    fingerprint_sha256 char(64) not null,
    estado text not null check (estado in (
        'PENDENTE',
        'EM_PROCESSAMENTO',
        'AGUARDANDO_REPROCESSAMENTO',
        'AGUARDANDO_ACAO_MANUAL',
        'RESULTADO_INDETERMINADO',
        'CONCLUIDA',
        'FALHA_DEFINITIVA',
        'CANCELADA'
    )),
    tipo_disparo_pendente text null check (tipo_disparo_pendente in ('AUTOMATICO', 'INTEGRAR_AGORA', 'REPROCESSAMENTO')),
    proxima_tentativa_em timestamptz null,
    fencing_token bigint not null default 0,
    worker_proprietario text null,
    lease_expira_em timestamptz null,
    fase_envio text not null check (fase_envio in ('NAO_INICIADO', 'ENVIO_INICIADO', 'RESULTADO_RECEBIDO')),
    tentativa_atual_id uuid null,
    correlation_id text not null,
    criado_em timestamptz not null default clock_timestamp(),
    atualizado_em timestamptz not null default clock_timestamp(),
    constraint uq_integracao_fingerprint unique (fingerprint_sha256),
    constraint ck_tipo_disparo_por_estado check (
        (estado = 'PENDENTE' and tipo_disparo_pendente in ('AUTOMATICO', 'INTEGRAR_AGORA'))
        or (estado = 'AGUARDANDO_REPROCESSAMENTO' and tipo_disparo_pendente in ('AUTOMATICO', 'REPROCESSAMENTO'))
        or (estado not in ('PENDENTE', 'AGUARDANDO_REPROCESSAMENTO') and tipo_disparo_pendente is null)
    ),
    -- F6: correlation_id interno e string nao vazia (contrato Hera). Nao e uuid.
    constraint ck_correlation_id_nao_vazio check (length(btrim(correlation_id)) > 0),
    -- F4: ownership coerente com o estado. EM_PROCESSAMENTO exige posse completa;
    -- fora de processamento nao mantem worker/lease ativos. fencing historico e tentativa_atual_id
    -- podem permanecer como referencia fora do estado.
    constraint ck_ownership_por_estado check (
        (estado = 'EM_PROCESSAMENTO'
            and fencing_token > 0
            and worker_proprietario is not null
            and lease_expira_em is not null
            and tentativa_atual_id is not null)
        or (estado <> 'EM_PROCESSAMENTO'
            and worker_proprietario is null
            and lease_expira_em is null)
    ),
    -- F5: agendamento coerente. Somente estados enfileiraveis carregam proxima_tentativa_em.
    constraint ck_agendamento_por_estado check (
        (estado in ('PENDENTE', 'AGUARDANDO_REPROCESSAMENTO') and proxima_tentativa_em is not null)
        or (estado not in ('PENDENTE', 'AGUARDANDO_REPROCESSAMENTO') and proxima_tentativa_em is null)
    )
);

create table tentativa_integracao (
    id uuid primary key,
    integracao_id uuid not null references integracao(id),
    fencing_token bigint not null,
    tipo_disparo text not null check (tipo_disparo in ('AUTOMATICO', 'INTEGRAR_AGORA', 'REPROCESSAMENTO')),
    fase_envio text not null check (fase_envio in ('NAO_INICIADO', 'ENVIO_INICIADO', 'RESULTADO_RECEBIDO')),
    desfecho text not null check (desfecho in ('EM_EXECUCAO', 'SUCESSO', 'FALHA', 'INTERROMPIDA', 'INDETERMINADA')),
    criada_em timestamptz not null,
    envio_iniciado_em timestamptz null,
    resultado_recebido_em timestamptz null
);

alter table integracao
    add constraint fk_integracao_tentativa_atual foreign key (tentativa_atual_id) references tentativa_integracao(id);

create table auditoria_integracao (
    id uuid primary key,
    integracao_id uuid null references integracao(id),
    evento text not null,
    criada_em timestamptz not null,
    worker text null,
    fencing_token bigint null,
    tentativa_id uuid null references tentativa_integracao(id),
    correlation_id text null
);

create table pausa_processamento (
    id uuid primary key,
    escopo text not null check (escopo in ('GERAL', 'SISTEMA_EXTERNO', 'OPERACAO')),
    sistema_externo text null,
    operacao text null,
    ativa boolean not null,
    criada_em timestamptz not null,
    retomada_em timestamptz null,
    constraint ck_pausa_escopo check (
        (escopo = 'GERAL' and sistema_externo is null and operacao is null)
        or (escopo = 'SISTEMA_EXTERNO' and sistema_externo is not null and operacao is null)
        or (escopo = 'OPERACAO' and sistema_externo is null and operacao is not null)
    )
);

-- Impede duplicidade de pausa vigente (ativa) por escopo/chave. PostgreSQL 18: unique parcial.
-- No maximo uma GERAL ativa; uma SISTEMA_EXTERNO ativa por sistema; uma OPERACAO ativa por operacao.
create unique index uq_pausa_geral_ativa on pausa_processamento (escopo) where ativa and escopo = 'GERAL';
create unique index uq_pausa_sistema_ativa on pausa_processamento (sistema_externo) where ativa and escopo = 'SISTEMA_EXTERNO';
create unique index uq_pausa_operacao_ativa on pausa_processamento (operacao) where ativa and escopo = 'OPERACAO';

create index ix_integracao_scan_claim on integracao (estado, tipo_disparo_pendente, proxima_tentativa_em, lease_expira_em);
create index ix_integracao_recovery on integracao (estado, fase_envio, lease_expira_em);
create unique index uq_tentativa_integracao_fencing on tentativa_integracao (integracao_id, fencing_token);
create index ix_tentativa_integracao on tentativa_integracao (integracao_id, criada_em);
create index ix_auditoria_integracao on auditoria_integracao (integracao_id, criada_em);
create index ix_pausa_ativa on pausa_processamento (ativa, escopo, sistema_externo, operacao);

-- S3: selecao pre-claim nao muta; validacao ocorre na aplicacao; claim especifico revalida
-- estado, tipo_disparo, proxima_tentativa_em vencida, pausa e ownership em TX curta.
-- Lease renewal, heartbeat e send-marker enforcement pertencem ao S4.
-- Uma pausa SISTEMA_EXTERNO = X bloqueia a integracao se origem = X OU destino = X (middleware bidirecional);
-- uma pausa OPERACAO = Y bloqueia por operacao, independentemente de origem/destino; GERAL bloqueia tudo.
-- F3: o claim posterior a validacao NAO pode fazer ORDER BY / LIMIT 1 sobre qualquer candidata elegivel.
-- Ha duas etapas distintas e separadas.

-- ETAPA 1 - SCAN PRE-CLAIM (somente descoberta, SEM mutacao, SEM claim). Usa relogio autoritativo do banco.
-- Exemplo documental, nao executado no Gate 005-B:
-- select i.id, i.fingerprint_sha256, i.estado, i.tipo_disparo_pendente
-- from integracao i
-- where i.estado in ('PENDENTE', 'AGUARDANDO_REPROCESSAMENTO')
--   and i.proxima_tentativa_em is not null
--   and i.proxima_tentativa_em <= clock_timestamp()
--   and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
--   and not exists (
--       select 1 from pausa_processamento p
--       where p.ativa and (
--           p.escopo = 'GERAL'
--           or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
--           or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
--       )
--   )
-- order by i.criado_em;
-- A validacao ocorre na Aplicacao, sem transacao longa.

-- ETAPA 2 - CLAIM ESPECIFICO da integracao validada (@integracao_id). Transacao curta.
-- Trava exatamente a linha @integracao_id com FOR UPDATE SKIP LOCKED; se estiver lockada,
-- claim=0 para esse candidato (NAO pega outra linha no lugar) e volta ao ciclo normal.
-- Revalida id, fingerprint, estado, tipo_disparo, agenda vencida, pausa e ownership antes de mutar.
-- BEGIN;
-- Exemplo documental, nao executado no Gate 005-B:
-- select i.id,
--        i.fencing_token + 1 as novo_fencing_token,
--        i.tipo_disparo_pendente as tipo_disparo_claim
-- from integracao i
-- where i.id = :integracao_id
--   and i.fingerprint_sha256 = :fingerprint_esperado
--   and i.estado = :estado_esperado
--   and (i.tipo_disparo_pendente is not distinct from :tipo_disparo_esperado)
--   and i.proxima_tentativa_em is not null
--   and i.proxima_tentativa_em <= clock_timestamp()
--   and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
--   and not exists (
--       select 1 from pausa_processamento p
--       where p.ativa and (
--           p.escopo = 'GERAL'
--           or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
--           or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
--       )
--   )
-- for update skip locked;
-- ROWS_AFFECTED=0 (linha lockada ou qualquer condicao divergente) => claim=0, sem fallback para outra candidata.
-- O chamador materializa :novo_fencing_token e :tipo_disparo_claim a partir do SELECT acima na mesma transacao.
-- insert into tentativa_integracao (id, integracao_id, fencing_token, tipo_disparo, fase_envio, desfecho, criada_em)
-- values (:nova_tentativa_id, :integracao_id, :novo_fencing_token, :tipo_disparo_claim,
--        'NAO_INICIADO', 'EM_EXECUCAO', clock_timestamp());
-- update integracao i
-- set estado = 'EM_PROCESSAMENTO',
--     fencing_token = :novo_fencing_token,
--     worker_proprietario = :worker,
--     lease_expira_em = clock_timestamp() + :lease_duration,
--     tentativa_atual_id = :nova_tentativa_id,
--     fase_envio = 'NAO_INICIADO',
--     proxima_tentativa_em = null,
--     tipo_disparo_pendente = null,
--     atualizado_em = clock_timestamp()
-- where i.id = :integracao_id
--   and i.fingerprint_sha256 = :fingerprint_esperado
--   and i.estado = :estado_esperado
--   and (i.tipo_disparo_pendente is not distinct from :tipo_disparo_esperado)
--   and i.proxima_tentativa_em is not null
--   and i.proxima_tentativa_em <= clock_timestamp()
--   and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
--   and not exists (
--       select 1 from pausa_processamento p
--       where p.ativa and (
--           p.escopo = 'GERAL'
--           or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
--           or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
--       )
--   )
-- returning i.id, i.fencing_token;
-- Insert da tentativa + update da integracao vivem na MESMA transacao curta; COMMIT ao final.
-- Se o insert falhar, o update nao ocorre. Se o update falhar, ROLLBACK remove o insert.
-- A tentativa_atual_id so e gravada depois que :nova_tentativa_id ja existe, compativel com FK imediata.
-- COMMIT;

-- Escritas pos-processamento devem sempre condicionar por id, tentativa_atual_id,
-- fencing_token, worker_proprietario e estado esperado. Nao ha fallback sem fencing.

-- Registrar candidato: inserir idempotentemente por fingerprint_sha256 informado pelo detector.
-- Nao ha formula universal de fingerprint neste SQL. A constraint uq_integracao_fingerprint
-- e a ultima linha de defesa local; prova concorrencial real pertence ao 005-C.
-- S4: ownership completo exige integracao, tentativa, fencing, worker, estado EM_PROCESSAMENTO
-- e lease_expira_em > clock_timestamp(). Lease vencida equivale a ownership expirado,
-- mesmo sem takeover por outro worker. Nenhuma TX DB atravessa ProcessarAsync, delay de heartbeat,
-- timeout operacional ou rede futura.

-- S4 renewal documental: operacao curta, fenced, sem incrementar fencing.
-- update integracao i
-- set lease_expira_em = clock_timestamp() + :lease_duration,
--     atualizado_em = clock_timestamp()
-- where i.id = :integracao_id
--   and i.estado = 'EM_PROCESSAMENTO'
--   and i.tentativa_atual_id = :tentativa_id
--   and i.fencing_token = :fencing_token
--   and i.worker_proprietario = :worker
--   and i.lease_expira_em > clock_timestamp()
-- returning i.id;
-- ROWS_AFFECTED=0: rejeitar fail-closed; nao reanimar lease expirada.

-- S4 send marker documental: processor somente inicia se este UPDATE fenced afetar 1 linha.
-- update integracao i
-- set fase_envio = 'ENVIO_INICIADO',
--     atualizado_em = clock_timestamp()
-- where i.id = :integracao_id
--   and i.estado = 'EM_PROCESSAMENTO'
--   and i.fase_envio = 'NAO_INICIADO'
--   and i.tentativa_atual_id = :tentativa_id
--   and i.fencing_token = :fencing_token
--   and i.worker_proprietario = :worker
--   and i.lease_expira_em > clock_timestamp()
-- returning i.id;
-- Marker duplicado ou fase diferente: ROWS_AFFECTED=0, fail-closed.

-- S4 persistencia de resultado conhecido: exige marker previo e ownership vigente.
-- update integracao i
-- set estado = :novo_estado,
--     tipo_disparo_pendente = :tipo_disparo_pendente,
--     proxima_tentativa_em = :proxima_tentativa_em,
--     worker_proprietario = null,
--     lease_expira_em = null,
--     fase_envio = 'RESULTADO_RECEBIDO',
--     atualizado_em = clock_timestamp()
-- where i.id = :integracao_id
--   and i.estado = 'EM_PROCESSAMENTO'
--   and i.fase_envio = 'ENVIO_INICIADO'
--   and i.tentativa_atual_id = :tentativa_id
--   and i.fencing_token = :fencing_token
--   and i.worker_proprietario = :worker
--   and i.lease_expira_em > clock_timestamp()
-- returning i.id;
-- update tentativa_integracao set fase_envio = 'RESULTADO_RECEBIDO', desfecho = :SUCESSO_OU_FALHA,
--     resultado_recebido_em = clock_timestamp()
-- where id = :tentativa_id and integracao_id = :integracao_id and fencing_token = :fencing_token;
-- Nao ha UPDATE alternativo sem fencing.

-- S4 timeout/excecao pos-marker sem resultado conclusivo: persistir indeterminado somente com authority vigente.
-- Nao marcar fase_envio como RESULTADO_RECEBIDO quando nenhum resultado externo conclusivo foi comprovado.
-- Estado RESULTADO_INDETERMINADO nao agenda retry automatico.

-- S4 recovery pre-send: lease expirada, EM_PROCESSAMENTO, NAO_INICIADO.
-- tentativa atual -> INTERROMPIDA; integracao -> AGUARDANDO_REPROCESSAMENTO/AUTOMATICO/proxima_tentativa_em=clock_timestamp();
-- limpar worker_proprietario, lease_expira_em e tentativa_atual_id; nao resetar fencing.

-- S4 recovery post-marker: lease expirada, EM_PROCESSAMENTO, ENVIO_INICIADO, sem resultado conclusivo.
-- tentativa atual -> INDETERMINADA; integracao -> RESULTADO_INDETERMINADO; tipo_disparo_pendente=null;
-- proxima_tentativa_em=null; limpar owner/lease; AUTO_RETRY=0.

-- S4 recovery com resultado conhecido ja persistido: NOVA_CHAMADA_EXTERNA=0.
-- Resultado recebido/concluido/falha conhecida nao reenvia por restart ou lease expiry.
