-- 007S3 - Fluxo SD: remessas criadas no SAP a partir das pesagens devolvidas.
--
-- Por que esta tabela existe:
-- remessa criada no SAP nao se desfaz por aqui. Uma pesagem gera UMA remessa, e a unicidade de
-- id_pesagem e o que garante isso - mesma logica de 007S1 (protocolo) e 007S2 (devolutiva).
--
-- Guarda tambem a recusa: o SAP devolve a causa numa mensagem de severidade "info" dentro de
-- errordetails, e so o codigo do topo (a consequencia) nao ajuda ninguem a corrigir. Por isso
-- mensagens_sap e texto livre com a lista inteira, na ordem em que ele devolveu.
begin;

set role fugates_dev_owner;

create table fugates.remessa_sd (
 id uuid not null,
 -- Pesagem de origem. E a chave que impede a segunda remessa.
 id_pesagem text not null,
 chave_natural text not null,
 ordem_venda text not null,
 linha_ordem text not null,
 -- 60 a 67, conforme a planilha de fluxo. E ele que escolhe litro ou quilo.
 setor_atividade text not null,
 -- Exatamente o que foi enviado: "1"/"L" no biodiesel, peso/"KG" nos demais.
 quantidade text not null,
 unidade text not null,
 -- Devolvido pelo SAP. Nulo quando a criacao nao se concluiu.
 numero_remessa text null,
 estado text not null,
 http_status smallint not null,
 payload_sha256 text not null,
 mensagens_sap text null,
 criado_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_remessa_sd primary key (id),
 -- Uma pesagem, uma remessa.
 constraint uq_remessa_sd_pesagem unique (id_pesagem),
 -- Um numero de remessa pertence a uma pesagem so.
 constraint uq_remessa_sd_numero unique (numero_remessa),
 constraint ck_remessa_sd_estado
   check (estado in ('CRIADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO')),
 -- CRIADA exige o numero: "criada sem numero" seria uma remessa que nao sabemos localizar.
 constraint ck_remessa_sd_criada_tem_numero
   check (estado <> 'CRIADA' or numero_remessa is not null),
 constraint ck_remessa_sd_chave_consistente
   check (chave_natural = ordem_venda || '/' || linha_ordem),
 constraint ck_remessa_sd_ordem_normalizada
   check (ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_remessa_sd_linha_normalizada
   check (linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 constraint ck_remessa_sd_setor check (setor_atividade in ('60','61','62','63','64','65','66','67')),
 constraint ck_remessa_sd_unidade check (unidade in ('L', 'KG')),
 -- Biodiesel (63) sempre 1 litro; os demais sempre quilo. A regra da planilha, no banco.
 constraint ck_remessa_sd_fluxo
   check ((setor_atividade = '63' and unidade = 'L' and quantidade = '1')
       or (setor_atividade <> '63' and unidade = 'KG')),
 constraint ck_remessa_sd_sha256 check (payload_sha256 ~ '^[0-9A-F]{64}$')
);

create index ix_remessa_sd_criado_em on fugates.remessa_sd (criado_em desc);
create index ix_remessa_sd_chave on fugates.remessa_sd (chave_natural);

revoke all privileges on table fugates.remessa_sd from public;
grant select, insert, update on table fugates.remessa_sd to fugates_acm_hml_app;
grant select, insert, update on table fugates.remessa_sd to fugates_sd_app;

-- O setor vem da ordem no momento do protocolo. Guardar aqui evita uma consulta ao SAP depois,
-- e registra o que valia quando a decisao foi tomada. Nulavel: os protocolos ja criados nao o
-- tem, e inventar um valor para eles seria pior do que admitir a ausencia.
alter table fugates.protocolo_sd_ordem_venda add column setor_atividade text null;
alter table fugates.protocolo_sd_ordem_venda add constraint ck_protocolo_sd_setor
  check (setor_atividade is null or setor_atividade in ('60','61','62','63','64','65','66','67'));

reset role;

commit;
