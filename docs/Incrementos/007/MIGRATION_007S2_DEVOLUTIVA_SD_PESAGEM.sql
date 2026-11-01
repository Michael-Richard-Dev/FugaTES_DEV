-- 007S2 - Fluxo SD: pesagens devolvidas pelo Guardian apos a balanca.
--
-- Por que esta tabela existe:
-- o Guardian pode reenviar o mesmo POST, e receber duas vezes nao pode virar duas remessas no
-- SAP. A unicidade de id_pesagem e o que impede isso - do mesmo jeito que a chave natural impede
-- protocolo duplicado em 007S1.
--
-- A correlacao com a ordem de venda vem dos campos adicionais 1003 (OrdemVendas) e 1009
-- (SD_ItDocV) e fica NULAVEL de proposito: devolutiva sem correlacao e guardada e sinalizada,
-- nunca descartada nem adivinhada. Correlacionar por aproximacao (parceiro, centro e data)
-- acerta no teste e erra em producao quando ha duas ordens do mesmo cliente no dia.
--
-- payload_json e TEXT, nao jsonb: e a evidencia do que chegou, byte a byte. jsonb reordena
-- chaves e descarta espacos, e ai deixa de ser o documento original.
begin;

set role fugates_dev_owner;

create table fugates.devolutiva_sd_pesagem (
 id uuid not null,
 -- Chave do evento no Guardian. E o que reconhece um reenvio.
 id_pesagem text not null,
 centro text not null,
 tipo_pesagem text not null,
 -- Sem fuso no wire: a hora local e preservada como veio, sem inventar UTC.
 data_pesagem timestamp not null,
 peso_bruto numeric(18,3) not null,
 peso_tara numeric(18,3) not null,
 -- Vem pronto da balanca, com o desconto de qualidade ja aplicado. Nao e bruto menos tara.
 peso_liquido numeric(18,3) not null,
 -- Correlacao, ja normalizada, no mesmo formato de protocolo_sd_ordem_venda.
 chave_natural text null,
 ordem_venda text null,
 linha_ordem text null,
 placa text null,
 nome_motorista text null,
 estado text not null,
 payload_json text not null,
 recebido_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_devolutiva_sd_pesagem primary key (id),
 -- O bloqueio de reprocessamento.
 constraint uq_devolutiva_sd_pesagem_evento unique (id_pesagem),
 constraint ck_devolutiva_sd_pesagem_tipo check (tipo_pesagem = 'S'),
 constraint ck_devolutiva_sd_pesagem_estado
   check (estado in ('CORRELACIONADA', 'SEM_CORRELACAO')),
 -- Os tres campos da correlacao andam juntos: ou todos preenchidos, ou nenhum.
 constraint ck_devolutiva_sd_pesagem_correlacao_completa
   check ((chave_natural is null and ordem_venda is null and linha_ordem is null)
       or (chave_natural is not null and ordem_venda is not null and linha_ordem is not null)),
 -- O estado tem de corresponder a existencia da chave, e nao ser um rotulo solto.
 constraint ck_devolutiva_sd_pesagem_estado_confere
   check ((estado = 'CORRELACIONADA') = (chave_natural is not null)),
 constraint ck_devolutiva_sd_pesagem_chave_consistente
   check (chave_natural is null or chave_natural = ordem_venda || '/' || linha_ordem),
 constraint ck_devolutiva_sd_pesagem_ordem_normalizada
   check (ordem_venda is null or ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_devolutiva_sd_pesagem_linha_normalizada
   check (linha_ordem is null or linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 constraint ck_devolutiva_sd_pesagem_centro check (centro in ('3011', '3008')),
 constraint ck_devolutiva_sd_pesagem_pesos
   check (peso_bruto >= 0 and peso_tara >= 0 and peso_liquido >= 0)
);

-- A tela lista por chegada; sem isto a ordenacao varre a tabela inteira.
create index ix_devolutiva_sd_pesagem_recebido_em
  on fugates.devolutiva_sd_pesagem (recebido_em desc);

-- Procurar as devolutivas de uma linha de ordem e a consulta do fluxo seguinte (remessa).
create index ix_devolutiva_sd_pesagem_chave
  on fugates.devolutiva_sd_pesagem (chave_natural)
  where chave_natural is not null;

revoke all privileges on table fugates.devolutiva_sd_pesagem from public;

-- O receptor HTTP roda na VM como fugates_acm_hml_app: grava o que chega e atualiza a
-- correlacao. Sem DELETE nem TRUNCATE - devolutiva recebida e fato, nao rascunho.
grant select, insert, update on table fugates.devolutiva_sd_pesagem to fugates_acm_hml_app;

-- A tela do desktop so acompanha.
grant select on table fugates.devolutiva_sd_pesagem to fugates_sd_app;

reset role;

commit;
