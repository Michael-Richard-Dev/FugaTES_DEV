-- R169 - Remove o contrato SINTETICO 9999000001, criado no teste de recepcao de 29/10/2026.
--
-- POR QUE
--   O contrato foi gerado por mim para provar a cadeia SAP -> FugaTES -> banco unificado. Ele nao
--   existe no SAP e sua integracao ficou em PENDENTE. O operador pediu a remocao.
--
-- ESCOPO: exclusivamente as linhas cuja chave e '9999000001'. Qualquer divergencia aborta e nada
-- e removido - a transacao so commita se o estado final for exatamente o esperado.
--
-- NAO remove nenhum outro contrato, nao mexe em roles, privilegios ou configuracao.

\set ON_ERROR_STOP on

BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='5s';

DO $guarda$
DECLARE v_versao int; v_ident int; v_integ int; v_total_antes int;
BEGIN
  IF current_database() <> 'fugates_dev'
     OR current_setting('port') <> '5433'
     OR current_setting('cluster_name') <> '18/fugatesacmdev'
     OR pg_is_in_recovery() THEN
    RAISE EXCEPTION 'REMOCAO_TESTE_TARGET_REJECTED';
  END IF;

  SELECT count(*) INTO v_versao FROM fugates.contrato_sap_toledo_versao WHERE numero_contrato='9999000001';
  SELECT count(*) INTO v_ident  FROM fugates.contrato_sap_toledo_identidade WHERE numero_contrato='9999000001';
  SELECT count(*) INTO v_integ  FROM fugates.integracao WHERE chave_negocio='9999000001';
  SELECT count(*) INTO v_total_antes FROM fugates.contrato_sap_toledo_versao;

  -- O estado tem que ser exatamente o observado antes de escrever este script.
  IF v_versao <> 1 OR v_ident <> 1 OR v_integ <> 1 THEN
    RAISE EXCEPTION 'REMOCAO_TESTE_ESTADO_INESPERADO: versao=% identidade=% integracao=%', v_versao, v_ident, v_integ;
  END IF;

  -- Nada de tentativas ou auditoria ligadas a ele; se existirem, parar e reavaliar.
  IF EXISTS (SELECT 1 FROM fugates.tentativa_integracao t
               JOIN fugates.integracao i ON i.id=t.integracao_id WHERE i.chave_negocio='9999000001')
     OR EXISTS (SELECT 1 FROM fugates.auditoria_integracao a
               JOIN fugates.integracao i ON i.id=a.integracao_id WHERE i.chave_negocio='9999000001') THEN
    RAISE EXCEPTION 'REMOCAO_TESTE_TEM_DEPENDENTES';
  END IF;

  RAISE NOTICE 'contratos antes: %', v_total_antes;
END; $guarda$;

-- Ordem segura: filho primeiro.
DELETE FROM fugates.contrato_sap_toledo_versao     WHERE numero_contrato='9999000001';
DELETE FROM fugates.contrato_sap_toledo_identidade WHERE numero_contrato='9999000001';
DELETE FROM fugates.integracao                     WHERE chave_negocio ='9999000001';

DO $postcheck$
DECLARE v_resto int; v_total_depois int;
BEGIN
  SELECT count(*) INTO v_resto
    FROM (SELECT 1 FROM fugates.contrato_sap_toledo_versao WHERE numero_contrato='9999000001'
          UNION ALL SELECT 1 FROM fugates.contrato_sap_toledo_identidade WHERE numero_contrato='9999000001'
          UNION ALL SELECT 1 FROM fugates.integracao WHERE chave_negocio='9999000001') x;
  IF v_resto <> 0 THEN
    RAISE EXCEPTION 'REMOCAO_TESTE_INCOMPLETA: % linhas restantes', v_resto;
  END IF;

  -- Os contratos legitimos continuam todos la.
  SELECT count(*) INTO v_total_depois FROM fugates.contrato_sap_toledo_versao;
  IF v_total_depois <> 6 THEN
    RAISE EXCEPTION 'REMOCAO_TESTE_AFETOU_OUTROS: esperados 6 contratos, encontrados %', v_total_depois;
  END IF;

  RAISE NOTICE 'contratos depois: % (os 6 legitimos preservados)', v_total_depois;
END; $postcheck$;

COMMIT;
