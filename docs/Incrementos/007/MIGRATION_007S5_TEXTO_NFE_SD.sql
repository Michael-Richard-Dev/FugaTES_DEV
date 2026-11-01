-- 007S5 - Fluxo SD, passo 5: texto de informacoes complementares da NF-e (ZNFE).
--
-- O texto nasce da ordem de venda e a balanca PREENCHE os espacos vazios dele. O PATCH no SAP
-- substitui o texto inteiro, entao o risco aqui nao e duplicar: e apagar o que veio do negocio.
--
-- Por isso texto_sha256 guarda a impressao digital do texto FINAL enviado. Com ela da para
-- provar depois que o que foi gravado e o que a tela mostrou na previa.
--
-- texto_criou diz se foi POST (nao havia texto) ou PATCH (havia). Sao caminhos diferentes no
-- SAP, e saber qual ocorreu muda o diagnostico quando algo sai errado.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column texto_estado text null;
alter table fugates.remessa_sd add column texto_http_status smallint null;
alter table fugates.remessa_sd add column texto_mensagens text null;
alter table fugates.remessa_sd add column texto_sha256 text null;
alter table fugates.remessa_sd add column texto_criou boolean null;
alter table fugates.remessa_sd add column texto_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_estado
  check (texto_estado is null
      or texto_estado in ('GRAVADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_exige_criada
  check (texto_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_completo
  check ((texto_estado is null and texto_em is null and texto_sha256 is null)
      or (texto_estado is not null and texto_em is not null and texto_sha256 is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_sha256
  check (texto_sha256 is null or texto_sha256 ~ '^[0-9A-F]{64}$');

reset role;

commit;
