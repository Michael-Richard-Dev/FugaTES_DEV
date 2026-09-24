begin;

do $h5_precheck$
begin
    if to_regclass('fugates.correlacao_guardian_sap') is null then
        raise exception 'H5 requires the applied F3 correlation table';
    end if;
    if to_regclass('fugates.contrato_sap_toledo_identidade') is null
       or to_regclass('fugates.contrato_sap_toledo_versao') is null then
        raise exception 'H5 requires the applied H4 tables';
    end if;
    if (select count(*) from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap') <> 20 then
        raise exception 'H5 requires the exact 20-column F3 baseline';
    end if;
    if exists (select 1 from information_schema.columns
        where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
          and column_name in ('protocolo_criado_em', 'tipo_contrato', 'url_para_retorno')) then
        raise exception 'H5 columns already exist';
    end if;
end
$h5_precheck$;

alter table fugates.correlacao_guardian_sap
    add column protocolo_criado_em timestamptz null,
    add column tipo_contrato text null,
    add column url_para_retorno text null;

comment on column fugates.correlacao_guardian_sap.protocolo_criado_em is
    'Timestamp from APIProtocoloPreCadastro response.dataCadastro; NULL until a valid response is durably associated.';

comment on column fugates.correlacao_guardian_sap.tipo_contrato is
    'Exact receipt TipoContrato sent as Guardian additional field 1000; NULL for historical rows.';

comment on column fugates.correlacao_guardian_sap.url_para_retorno is
    'Exact configuracao.urlParaRetorno sent with the Guardian pre-registration; NULL for historical rows.';

-- Existing table-level SELECT and UPDATE grants for fugates_dev_app cover this column.
commit;
