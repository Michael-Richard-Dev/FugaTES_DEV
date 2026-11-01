-- 007S4 - Fluxo SD, passo 4: ajuste da remessa depois de criada.
--
-- O passo 4 edita o cabecalho da remessa (meio de transporte sempre; pesos da balanca so no
-- biodiesel) e, no biodiesel, zera os pesos de todos os itens. Conforme "APIS SD para FUGATES".
--
-- Por que colunas e nao tabela nova:
-- o ajuste pertence a UMA remessa e acontece no maximo uma vez por ela. Uma tabela separada
-- permitiria duas linhas de ajuste para a mesma remessa, que e justamente o que nao pode.
--
-- Por que tudo nulavel:
-- as remessas criadas antes desta migracao nao passaram pelo passo 4. Nulo diz "nao ajustada";
-- preencher com um valor qualquer diria "ajustada", que seria falso.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column ajuste_estado text null;
alter table fugates.remessa_sd add column ajuste_http_status smallint null;
alter table fugates.remessa_sd add column ajuste_mensagens text null;
-- Impressao digital do corpo enviado ao cabecalho. E o que permite conferir depois QUAL peso
-- foi escrito, sem guardar o documento inteiro.
alter table fugates.remessa_sd add column ajuste_payload_sha256 text null;
alter table fugates.remessa_sd add column ajuste_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_estado
  check (ajuste_estado is null
      or ajuste_estado in ('AJUSTADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO'));

-- So faz sentido ajustar o que foi criado: ajuste em remessa sem numero seria ajuste de nada.
alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_exige_criada
  check (ajuste_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

-- Desfecho registrado anda junto com a hora e o sha: um sem o outro deixaria o registro
-- incapaz de responder "quando" ou "o que foi escrito".
alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_completo
  check ((ajuste_estado is null and ajuste_em is null and ajuste_payload_sha256 is null)
      or (ajuste_estado is not null and ajuste_em is not null and ajuste_payload_sha256 is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_sha256
  check (ajuste_payload_sha256 is null or ajuste_payload_sha256 ~ '^[0-9A-F]{64}$');

reset role;

commit;
