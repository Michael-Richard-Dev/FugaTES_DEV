-- FugaTES Incremento 006 - H4 Durable Idempotency
-- Gate: INC006-H4-DB-ARTIFACTS-R1
-- Canonical source HEAD: f2d1802a59517211407d4dc143abccbdc13a85a8
--
-- Scope:
--   * H4 only: NumeroContrato identity + immutable version + fingerprint
--     + canonical rehydratable JSONB snapshot + integracao.id link.
--   * INC005 remains the operational authority for state, claim, lease,
--     fencing, attempt, send phase, indeterminate result and recovery.
--   * F3 is independent and MUST NOT be changed by this migration.
--
-- F3 protected artifact identities (filesystem gate, external to SQL):
--   MIGRATION_006F2_CORRELACAO_GUARDIAN_SAP.sql
--     E6273D7BB60866B334D6095ABC381CE7468CD05800905C5ACDD62661DF728329
--   ROLLBACK_006F2_CORRELACAO_GUARDIAN_SAP.sql
--     05D6B5BAEE860FC52A4DEC7712C01FC5E854F2B4B0CD47BECC19A30572F3BA60
--   VALIDATION_006F2_CORRELACAO_GUARDIAN_SAP.sql
--     1EECD9110EA30BEE8E57B4DBD7AD86E51754D04E8A13E8863D8939DF5C9B1D54
--
-- Registration transaction contract (implemented by Application/repository,
-- not by this DDL):
--   identity row lock
--   + fingerprint deduplication
--   + monotonic version allocation
--   + INC005 integracao INSERT
--   + H4 version INSERT
--   + ultima_versao UPDATE
-- MUST use the SAME PostgreSQL connection and SAME transaction.
--
-- Payload:
--   payload_contrato is the canonical Application snapshot of the 23 SAP
--   contract fields. It is NOT the raw HTTP request and MUST NOT contain
--   Authorization, credentials, secrets or tokens. PostgreSQL does NOT
--   recalculate the fingerprint from JSONB.
--
-- Fail-closed characteristics:
--   * no IF NOT EXISTS
--   * no CASCADE
--   * no change to fugates.integracao columns or existing INC005 triggers
--   * non-H4 integrations bypass the H4 claim guard unchanged

begin;

set local role fugates_dev_owner;
set local search_path = pg_catalog, fugates;

do $h4_preflight$
declare
    v_integracao_id_type text;
begin
    if to_regnamespace('fugates') is null then
        raise exception 'H4_PRECHECK_FAIL: schema fugates ausente';
    end if;

    if to_regclass('fugates.integracao') is null then
        raise exception 'H4_PRECHECK_FAIL: fugates.integracao ausente';
    end if;

    select a.atttypid::regtype::text
      into v_integracao_id_type
      from pg_catalog.pg_attribute a
     where a.attrelid = 'fugates.integracao'::regclass
       and a.attname = 'id'
       and a.attnum > 0
       and not a.attisdropped;

    if v_integracao_id_type is distinct from 'uuid' then
        raise exception
            'H4_PRECHECK_FAIL: fugates.integracao.id esperado uuid, encontrado %',
            coalesce(v_integracao_id_type, '<NULL>');
    end if;

    if not exists (
        select 1
          from pg_catalog.pg_roles
         where rolname = 'fugates_dev_owner'
    ) then
        raise exception 'H4_PRECHECK_FAIL: role fugates_dev_owner ausente';
    end if;

    if not exists (
        select 1
          from pg_catalog.pg_roles
         where rolname = 'fugates_dev_app'
    ) then
        raise exception 'H4_PRECHECK_FAIL: role fugates_dev_app ausente';
    end if;

    if to_regclass('fugates.contrato_sap_toledo_identidade') is not null then
        raise exception
            'H4_PRECHECK_FAIL: fugates.contrato_sap_toledo_identidade ja existe';
    end if;

    if to_regclass('fugates.contrato_sap_toledo_versao') is not null then
        raise exception
            'H4_PRECHECK_FAIL: fugates.contrato_sap_toledo_versao ja existe';
    end if;

    if to_regprocedure(
        'fugates.fn_h4_guard_claim_contrato_sap_toledo()'
    ) is not null then
        raise exception
            'H4_PRECHECK_FAIL: funcao H4 de claim ja existe';
    end if;

    if exists (
        select 1
          from pg_catalog.pg_trigger t
         where t.tgrelid = 'fugates.integracao'::regclass
           and t.tgname = 'trg_h4_guard_claim_contrato_sap_toledo'
           and not t.tgisinternal
    ) then
        raise exception
            'H4_PRECHECK_FAIL: trigger H4 de claim ja existe';
    end if;
end
$h4_preflight$;

create table fugates.contrato_sap_toledo_identidade
(
    numero_contrato text not null,
    ultima_versao bigint not null default 0,
    criado_em timestamptz not null default clock_timestamp(),

    constraint pk_contrato_sap_toledo_identidade
        primary key (numero_contrato),

    constraint ck_contrato_sap_toledo_identidade_numero_nao_vazio
        check (length(numero_contrato) > 0),

    constraint ck_contrato_sap_toledo_identidade_ultima_versao
        check (ultima_versao >= 0)
);

create table fugates.contrato_sap_toledo_versao
(
    numero_contrato text not null,
    versao bigint not null,
    fingerprint_sha256 text not null,
    payload_contrato jsonb not null,
    integracao_id uuid not null,
    criado_em timestamptz not null default clock_timestamp(),

    constraint pk_contrato_sap_toledo_versao
        primary key (numero_contrato, versao),

    constraint fk_contrato_sap_toledo_versao_identidade
        foreign key (numero_contrato)
        references fugates.contrato_sap_toledo_identidade(numero_contrato),

    constraint fk_contrato_sap_toledo_versao_integracao
        foreign key (integracao_id)
        references fugates.integracao(id),

    constraint uq_contrato_sap_toledo_numero_fingerprint
        unique (numero_contrato, fingerprint_sha256),

    constraint uq_contrato_sap_toledo_integracao_id
        unique (integracao_id),

    constraint ck_contrato_sap_toledo_versao_positiva
        check (versao > 0),

    constraint ck_contrato_sap_toledo_fingerprint_sha256
        check (
            length(fingerprint_sha256) = 64
            and fingerprint_sha256 ~ '^[0-9A-F]{64}$'
        ),

    constraint ck_contrato_sap_toledo_payload_object
        check (jsonb_typeof(payload_contrato) = 'object')
);

create function fugates.fn_h4_guard_claim_contrato_sap_toledo()
returns trigger
language plpgsql
security definer
set search_path = pg_catalog, fugates
as $h4_guard$
declare
    v_numero_contrato text;
    v_versao bigint;
begin
    -- Scope strictly limited to transitions into EM_PROCESSAMENTO.
    if not (
        old.estado is distinct from 'EM_PROCESSAMENTO'
        and new.estado = 'EM_PROCESSAMENTO'
    ) then
        return new;
    end if;

    -- Non-H4 integrations are completely unaffected.
    select v.numero_contrato,
           v.versao
      into v_numero_contrato,
           v_versao
      from fugates.contrato_sap_toledo_versao v
     where v.integracao_id = new.id;

    if not found then
        return new;
    end if;

    -- Serialize every H4 claim decision for the same NumeroContrato.
    perform 1
      from fugates.contrato_sap_toledo_identidade i
     where i.numero_contrato = v_numero_contrato
     for update;

    if not found then
        raise exception
            using
                errcode = '23503',
                message = format(
                    'H4_CLAIM_GUARD_FAIL: identidade ausente para NumeroContrato=%s',
                    v_numero_contrato
                );
    end if;

    -- A later version cannot start while ANY predecessor is non-terminal.
    -- Operational state remains authoritative in INC005 fugates.integracao.
    if exists (
        select 1
          from fugates.contrato_sap_toledo_versao anterior
          join fugates.integracao anterior_integracao
            on anterior_integracao.id = anterior.integracao_id
         where anterior.numero_contrato = v_numero_contrato
           and anterior.versao < v_versao
           and anterior_integracao.estado in (
               'PENDENTE',
               'EM_PROCESSAMENTO',
               'AGUARDANDO_REPROCESSAMENTO',
               'AGUARDANDO_ACAO_MANUAL',
               'RESULTADO_INDETERMINADO'
           )
    ) then
        -- Suppress only this attempted transition. The existing INC005
        -- claim transaction remains responsible for observing zero updated
        -- rows and rolling back its attempt/audit mutations.
        return null;
    end if;

    return new;
end
$h4_guard$;

revoke all
    on function fugates.fn_h4_guard_claim_contrato_sap_toledo()
    from public;

grant execute
    on function fugates.fn_h4_guard_claim_contrato_sap_toledo()
    to fugates_dev_app;

create trigger trg_h4_guard_claim_contrato_sap_toledo
before update of estado
on fugates.integracao
for each row
execute function fugates.fn_h4_guard_claim_contrato_sap_toledo();

-- Runtime ACL: identity row may be selected, inserted and have only
-- ultima_versao updated. No table-wide UPDATE grant is given.
revoke all
    on table fugates.contrato_sap_toledo_identidade
    from public;

revoke all
    on table fugates.contrato_sap_toledo_identidade
    from fugates_dev_app;

grant select, insert
    on table fugates.contrato_sap_toledo_identidade
    to fugates_dev_app;

grant update (ultima_versao)
    on table fugates.contrato_sap_toledo_identidade
    to fugates_dev_app;

-- Runtime ACL: immutable version rows are append-only.
revoke all
    on table fugates.contrato_sap_toledo_versao
    from public;

revoke all
    on table fugates.contrato_sap_toledo_versao
    from fugates_dev_app;

grant select, insert
    on table fugates.contrato_sap_toledo_versao
    to fugates_dev_app;

commit;
