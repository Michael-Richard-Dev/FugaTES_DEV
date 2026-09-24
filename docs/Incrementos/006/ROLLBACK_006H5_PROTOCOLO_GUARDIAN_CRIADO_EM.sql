begin;

lock table fugates.correlacao_guardian_sap in access exclusive mode;

do $h5_rollback$
begin
    if to_regclass('fugates.correlacao_guardian_sap') is null
       or to_regclass('fugates.contrato_sap_toledo_versao') is null then
        raise exception 'H5 rollback requires the applied F3/H4 baseline';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
          and ((column_name = 'protocolo_criado_em' and data_type = 'timestamp with time zone')
            or (column_name in ('tipo_contrato', 'url_para_retorno') and data_type = 'text'))) <> 3 then
        raise exception 'H5 columns are absent or have unexpected types';
    end if;
    if exists (select 1 from fugates.correlacao_guardian_sap
        where protocolo_criado_em is not null or tipo_contrato is not null or url_para_retorno is not null) then
        raise exception 'H5 rollback refused: H5 context or protocol timestamps exist';
    end if;
end
$h5_rollback$;

alter table fugates.correlacao_guardian_sap
    drop column protocolo_criado_em,
    drop column tipo_contrato,
    drop column url_para_retorno;

commit;
