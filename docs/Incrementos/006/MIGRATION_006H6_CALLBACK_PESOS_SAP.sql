begin;

do $h6_precheck$
begin
    if to_regclass('fugates.correlacao_guardian_sap') is null
       or to_regclass('fugates.contrato_sap_toledo_identidade') is null
       or to_regclass('fugates.contrato_sap_toledo_versao') is null then
        raise exception 'H6 requires applied F3, H4 and H5 baseline';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap') <> 23 then
        raise exception 'H6 requires the exact 23-column H5 baseline';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
          and column_name in ('protocolo_criado_em', 'tipo_contrato', 'url_para_retorno')) <> 3 then
        raise exception 'H6 requires all H5 columns';
    end if;
    if exists (select 1 from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
          and column_name in ('peso_bruto', 'tara_final', 'peso_liquido')) then
        raise exception 'H6 weight columns already exist';
    end if;
end
$h6_precheck$;

alter table fugates.correlacao_guardian_sap
    add column peso_bruto numeric null,
    add column tara_final numeric null,
    add column peso_liquido numeric null;

comment on column fugates.correlacao_guardian_sap.peso_bruto is
    'Exact accepted Guardian callback PesoBruto; nullable for historical rows.';
comment on column fugates.correlacao_guardian_sap.tara_final is
    'Exact accepted Guardian callback Tara; nullable for historical rows.';
comment on column fugates.correlacao_guardian_sap.peso_liquido is
    'Exact accepted Guardian callback PesoLiquido; nullable for historical rows.';

revoke all privileges on table fugates.correlacao_guardian_sap from public;
revoke all privileges on table fugates.correlacao_guardian_sap from fugates_dev_app;
grant select, insert, update on table fugates.correlacao_guardian_sap to fugates_dev_app;

commit;
