begin;
create table fugates.correlacao_guardian_sap (
 id uuid not null,
 documento_logistico_referencia text not null,
 protocolo_guardian text null,
 ticket_guardian text null,
 material_sap text not null,
 bp_emissor text not null,
 bp_transportadora text not null,
 centro_sap text not null,
 tipo_documento text not null,
 contrato_sap text not null,
 operacao_contrato text not null,
 tipo_operacao_sap smallint not null,
 contrato_acm_sap text null,
 pedido_compras_sap text null,
 processo text not null,
 direcao text not null,
 estado text not null,
 callback_fingerprint text null,
 criado_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_correlacao_guardian_sap primary key (id),
 constraint uq_correlacao_guardian_sap_documento unique (documento_logistico_referencia),
 constraint uq_correlacao_guardian_sap_protocolo unique (protocolo_guardian),
 constraint uq_correlacao_guardian_sap_ticket unique (ticket_guardian),
 constraint ck_correlacao_guardian_sap_processo check (processo = 'RECEBIMENTO'),
 constraint ck_correlacao_guardian_sap_direcao check (direcao = 'SAP_PARA_GUARDIAN'),
 constraint ck_correlacao_guardian_sap_estado check (estado in ('AGUARDANDO_PROTOCOLO', 'AGUARDANDO_TICKET', 'TICKET_ASSOCIADO'))
);

revoke all privileges
on table fugates.correlacao_guardian_sap
from public;

revoke all privileges
on table fugates.correlacao_guardian_sap
from fugates_dev_app;

grant select, insert, update
on table fugates.correlacao_guardian_sap
to fugates_dev_app;

commit;
