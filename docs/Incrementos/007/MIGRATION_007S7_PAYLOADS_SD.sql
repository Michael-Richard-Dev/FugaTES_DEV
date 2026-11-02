-- 007S7 - Fluxo SD: guardar o CORPO enviado em cada passo, e nao so a impressao digital.
--
-- Ate aqui guardavamos payload_sha256 de cada passo. Ele prova que o que foi enviado e o que a
-- tela mostrou na previa, mas nao mostra NADA a quem abre a tela depois. Com o fluxo passando a
-- rodar sozinho, ninguem ve a previa - e o operador precisa poder abrir a linha e ler o que foi
-- mandado ao SAP em cada etapa.
--
-- Tudo nulavel: as remessas criadas antes desta migracao nao tem o corpo guardado, e inventar um
-- a partir dos campos seria reconstrucao, nao registro.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column payload_json text null;
alter table fugates.remessa_sd add column ajuste_payload_json text null;
alter table fugates.remessa_sd add column texto_payload_json text null;

reset role;

commit;
