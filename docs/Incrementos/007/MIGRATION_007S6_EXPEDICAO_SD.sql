-- 007S6 - Fluxo SD, passo 6: picking e saida de mercadoria.
--
-- Esta e a migracao que guarda o passo IRREVERSIVEL. A saida de mercadoria move estoque e gera
-- documento de material: nao existe "desfazer" por API neste fluxo. Por isso saida_estado, uma
-- vez gravado, impede uma segunda tentativa pela tela - e por isso ele e reservado ANTES do POST,
-- com DESFECHO_DESCONHECIDO, e nao depois.
--
-- Picking e separado da saida de proposito. Sao dois momentos, e um picking confirmado nao
-- autoriza sozinho a saida; quem autoriza e quem clica.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column picking_estado text null;
alter table fugates.remessa_sd add column picking_http_status smallint null;
alter table fugates.remessa_sd add column picking_mensagens text null;
alter table fugates.remessa_sd add column picking_em timestamptz null;

alter table fugates.remessa_sd add column saida_estado text null;
alter table fugates.remessa_sd add column saida_http_status smallint null;
alter table fugates.remessa_sd add column saida_mensagens text null;
alter table fugates.remessa_sd add column saida_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_estado
  check (picking_estado is null
      or picking_estado in ('CONFIRMADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_estado
  check (saida_estado is null
      or saida_estado in ('POSTADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_exige_criada
  check (picking_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_exige_criada
  check (saida_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_completo
  check ((picking_estado is null) = (picking_em is null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_completo
  check ((saida_estado is null) = (saida_em is null));

reset role;

commit;
