-- FugaTES Incremento 005 Gate 005-C
-- Migration incremental P2F-E3G-R2B-DB.
-- REV5 permanece imutavel.
-- Evolucao unica autorizada: auditoria_integracao.diagnostico text NULL.

alter table fugates.auditoria_integracao
    add column diagnostico text null;
