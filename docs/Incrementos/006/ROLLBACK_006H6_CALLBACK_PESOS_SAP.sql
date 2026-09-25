begin;

lock table fugates.correlacao_guardian_sap in access exclusive mode;

do $h6_rollback$
begin
    if to_regclass('fugates.correlacao_guardian_sap') is null
       or to_regclass('fugates.contrato_sap_toledo_versao') is null then
        raise exception 'H6 rollback requires applied F3/H4/H5 baseline';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap') <> 26 then
        raise exception 'H6 rollback requires the exact 26-column baseline';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
          and column_name in ('peso_bruto', 'tara_final', 'peso_liquido')
          and data_type = 'numeric' and is_nullable = 'YES') <> 3 then
        raise exception 'H6 columns are absent or have unexpected types';
    end if;
    if exists (select 1 from fugates.correlacao_guardian_sap
        where peso_bruto is not null or tara_final is not null or peso_liquido is not null) then
        raise exception 'H6 rollback refused: accepted callback weights exist';
    end if;
end
$h6_rollback$;

alter table fugates.correlacao_guardian_sap
    drop column peso_bruto,
    drop column tara_final,
    drop column peso_liquido;

commit;
