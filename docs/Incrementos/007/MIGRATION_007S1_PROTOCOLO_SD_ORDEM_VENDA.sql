-- 007S1 - Fluxo SD: protocolos de pre-cadastro criados a partir de ordens de venda.
--
-- Por que esta tabela existe:
-- a varredura do SAP roda a cada 30 segundos e, sem registro duravel, a mesma linha de ordem
-- viraria um protocolo novo a cada volta. Protocolo criado no Guardian nao se desfaz.
--
-- A unicidade esta na CHAVE NATURAL (ordem + linha), ja NORMALIZADA - sem os zeros a esquerda
-- que o SAP preenche. Comparar a forma do SAP com a do Guardian foi exatamente o defeito que
-- deixou a guarda de duplicidade do ACM sem efeito ("1160" nunca casou com "0000001160").
--
-- Um envio sem desfecho conhecido (erro de rede) TAMBEM ocupa a chave: o POST pode ter chegado,
-- e supor que nao chegou e o que cria duplicata. Por isso protocolo_id e nulavel e o estado
-- DESFECHO_DESCONHECIDO existe.
begin;

-- Nao existe credencial de fugates_dev_owner nesta maquina (conferido em 31/10/2026): os quatro
-- arquivos de senha so tem papeis de aplicacao. Entao esta migracao e aplicada pelo superusuario,
-- via sudo -u postgres. O set role abaixo faz a tabela nascer pertencendo ao DONO DO SCHEMA, como
-- as demais - sem ele, ela pertenceria a postgres, e a manutencao futura ficaria fora do padrao.
-- Rodando ja como fugates_dev_owner, este comando e inofensivo.
set role fugates_dev_owner;

create table fugates.protocolo_sd_ordem_venda (
 id uuid not null,
 -- Chave natural normalizada: '<ordem>/<linha>', ex. '782/10'.
 chave_natural text not null,
 ordem_venda text not null,
 linha_ordem text not null,
 centro text not null,
 codigo_item_guardian text not null,
 codigo_emissor_guardian text not null,
 -- Devolvido pelo Guardian. Nulo quando o desfecho nao e conhecido ou o envio foi recusado.
 protocolo_id bigint null,
 estado text not null,
 http_status smallint not null,
 -- SHA-256 dos bytes efetivamente transmitidos. Prova o que foi enviado, sem guardar o payload.
 payload_sha256 text not null,
 url_retorno text not null,
 -- Mensagem do Guardian em caso de recusa. Nunca credencial, nunca token.
 mensagem_recusa text null,
 criado_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_protocolo_sd_ordem_venda primary key (id),
 -- O bloqueio de duplicidade. Uma linha de ordem ocupa a chave para sempre, qualquer que tenha
 -- sido o desfecho.
 constraint uq_protocolo_sd_ordem_venda_chave unique (chave_natural),
 -- Um protocolo do Guardian pertence a uma linha so.
 constraint uq_protocolo_sd_ordem_venda_protocolo unique (protocolo_id),
 constraint ck_protocolo_sd_ordem_venda_estado
   check (estado in ('CRIADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO')),
 -- Estado CRIADO exige o id devolvido: "criado sem id" seria um registro que nao rastreia nada.
 constraint ck_protocolo_sd_ordem_venda_criado_tem_id
   check (estado <> 'CRIADO' or protocolo_id is not null),
 -- A chave natural tem de ser a concatenacao dos dois campos, e nao um texto solto.
 constraint ck_protocolo_sd_ordem_venda_chave_consistente
   check (chave_natural = ordem_venda || '/' || linha_ordem),
 -- Normalizada: sem espacos, sem vazio e sem zero a esquerda.
 constraint ck_protocolo_sd_ordem_venda_ordem_normalizada
   check (ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_protocolo_sd_ordem_venda_linha_normalizada
   check (linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 -- Os unicos centros do fluxo SD.
 constraint ck_protocolo_sd_ordem_venda_centro check (centro in ('3011', '3008')),
 -- O retorno recebe dado de pesagem: so TLS.
 constraint ck_protocolo_sd_ordem_venda_url_https check (url_retorno like 'https://%'),
 constraint ck_protocolo_sd_ordem_venda_sha256 check (payload_sha256 ~ '^[0-9A-F]{64}$')
);

revoke all privileges
on table fugates.protocolo_sd_ordem_venda
from public;

revoke all privileges
on table fugates.protocolo_sd_ordem_venda
from fugates_dev_app;

-- Sem DELETE e sem TRUNCATE: apagar uma linha daqui liberaria o reenvio de um protocolo que ja
-- existe no Guardian. Correcao de registro e tarefa administrativa, nao do aplicativo.
grant select, insert, update
on table fugates.protocolo_sd_ordem_venda
to fugates_dev_app;

reset role;

commit;
