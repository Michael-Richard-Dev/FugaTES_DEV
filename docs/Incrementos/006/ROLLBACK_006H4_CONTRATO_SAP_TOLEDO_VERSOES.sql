-- FugaTES Incremento 006 - H4 Durable Idempotency
-- Gate: INC006-H4-DB-ARTIFACTS-R1
-- Rollback independent of F3.
--
-- FAIL CLOSED:
--   If ANY version row exists, rollback aborts before dropping any H4 object.
--   This script never deletes/updates fugates.integracao,
--   fugates.tentativa_integracao or fugates.auditoria_integracao.
--   No CASCADE is used.

begin;

set local role fugates_dev_owner;
set local search_path = pg_catalog, fugates;

do $h4_rollback_preflight$
begin
    if to_regclass('fugates.contrato_sap_toledo_identidade') is null then
        raise exception
            'H4_ROLLBACK_FAIL: tabela identidade H4 ausente';
    end if;

    if to_regclass('fugates.contrato_sap_toledo_versao') is null then
        raise exception
            'H4_ROLLBACK_FAIL: tabela versao H4 ausente';
    end if;

    if to_regprocedure(
        'fugates.fn_h4_guard_claim_contrato_sap_toledo()'
    ) is null then
        raise exception
            'H4_ROLLBACK_FAIL: funcao H4 de claim ausente';
    end if;

    if not exists (
        select 1
          from pg_catalog.pg_trigger t
         where t.tgrelid = 'fugates.integracao'::regclass
           and t.tgname = 'trg_h4_guard_claim_contrato_sap_toledo'
           and not t.tgisinternal
    ) then
        raise exception
            'H4_ROLLBACK_FAIL: trigger H4 de claim ausente';
    end if;

    if exists (
        select 1
          from fugates.contrato_sap_toledo_versao
         limit 1
    ) then
        raise exception
            'H4_ROLLBACK_FAIL_CLOSED: existem versoes H4; historico nao sera destruido automaticamente';
    end if;
end
$h4_rollback_preflight$;

drop trigger trg_h4_guard_claim_contrato_sap_toledo
    on fugates.integracao;

drop function fugates.fn_h4_guard_claim_contrato_sap_toledo();

drop table fugates.contrato_sap_toledo_versao;

drop table fugates.contrato_sap_toledo_identidade;

commit;
