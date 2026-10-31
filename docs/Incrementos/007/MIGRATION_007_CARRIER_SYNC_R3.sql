-- PROPOSTA GAIA R3. NAO EXECUTAR HML SEM LIBERACAO ESPECIFICA.
-- Schema fugates deve existir. Nao instala worker, nao toca INC005.
BEGIN;
-- HOTFIX R2: GAIA supplies the approved role/profile identity before execution.
-- Mandatory session settings: fugates.carrier_app_role, fugates.carrier_profile_role,
-- fugates.carrier_role_approval. No inferred owner and no desktop-role fallback.
DO $carrier_role_preflight$
DECLARE app_role text := nullif(current_setting('fugates.carrier_app_role',true),'');
BEGIN
 IF app_role IS NULL OR current_setting('fugates.carrier_profile_role',true) IS DISTINCT FROM app_role
    OR nullif(current_setting('fugates.carrier_role_approval',true),'') IS NULL THEN
  RAISE EXCEPTION 'CARRIER_APP_ROLE_PROFILE_OR_APPROVAL_MISSING';
 END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=app_role AND rolcanlogin AND NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb) THEN
  RAISE EXCEPTION 'CARRIER_APP_ROLE_INVALID';
 END IF;
 IF has_schema_privilege(app_role,'fugates','CREATE') THEN
  RAISE EXCEPTION 'CARRIER_APP_ROLE_SCHEMA_CREATE_FORBIDDEN';
 END IF;
END; $carrier_role_preflight$;
CREATE TABLE fugates.carrier_sync_cycle (
 id uuid PRIMARY KEY, started_utc timestamptz NOT NULL, finished_utc timestamptz,
 consulted integer NOT NULL DEFAULT 0 CHECK(consulted>=0), created integer NOT NULL DEFAULT 0 CHECK(created>=0),
 updated integer NOT NULL DEFAULT 0 CHECK(updated>=0), unchanged integer NOT NULL DEFAULT 0 CHECK(unchanged>=0),
 pending integer NOT NULL DEFAULT 0 CHECK(pending>=0), failed integer NOT NULL DEFAULT 0 CHECK(failed>=0),
 status varchar(40) NOT NULL
);
CREATE TABLE fugates.carrier_sync_state (
 bp varchar(40) PRIMARY KEY, name varchar(250) NOT NULL DEFAULT '', guardian_code varchar(100),
 content_hash char(64) NOT NULL, status varchar(40) NOT NULL, action varchar(20) NOT NULL,
 result_code varchar(100) NOT NULL, sap_checked_utc timestamptz NOT NULL, guardian_synced_utc timestamptz,
 pending_attempt_id uuid
);
CREATE TABLE fugates.carrier_sync_attempt (
 id uuid PRIMARY KEY, cycle_id uuid NOT NULL REFERENCES fugates.carrier_sync_cycle(id),
 bp varchar(40) NOT NULL REFERENCES fugates.carrier_sync_state(bp), action varchar(20) NOT NULL CHECK(action IN('CREATE','UPDATE')),
 expected_hash char(64) NOT NULL, guardian_code varchar(100), started_utc timestamptz NOT NULL, resolved_utc timestamptz,
 status varchar(40) NOT NULL DEFAULT 'WRITE_IN_FLIGHT'
);
ALTER TABLE fugates.carrier_sync_state ADD CONSTRAINT carrier_pending_attempt_fk FOREIGN KEY(pending_attempt_id) REFERENCES fugates.carrier_sync_attempt(id) DEFERRABLE INITIALLY DEFERRED;
CREATE UNIQUE INDEX carrier_sync_one_pending_bp ON fugates.carrier_sync_attempt(bp) WHERE resolved_utc IS NULL;
CREATE TABLE fugates.carrier_sync_step (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, cycle_id uuid NOT NULL REFERENCES fugates.carrier_sync_cycle(id),
 bp varchar(40) NOT NULL, step varchar(40) NOT NULL CHECK(step IN('SAP_GET','GUARDIAN_GET','GUARDIAN_POST','GUARDIAN_PUT','GUARDIAN_READBACK','RESULTADO_FINAL')),
 result_code varchar(100) NOT NULL CHECK(result_code ~ '^[A-Za-z0-9_]+$'), occurred_utc timestamptz NOT NULL
);
CREATE INDEX carrier_sync_step_cycle ON fugates.carrier_sync_step(cycle_id,occurred_utc,id);
CREATE INDEX carrier_sync_step_bp ON fugates.carrier_sync_step(bp,occurred_utc DESC,id DESC);
CREATE INDEX carrier_sync_state_updated ON fugates.carrier_sync_state(sap_checked_utc DESC,bp);
CREATE TABLE fugates.carrier_sync_checkpoint (
 singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton), watermark timestamptz, upper_bound timestamptz,
 page_cursor text, visited_pages text[] NOT NULL DEFAULT '{}', enabled boolean NOT NULL DEFAULT false, heartbeat_utc timestamptz, next_cycle_utc timestamptz
);
INSERT INTO fugates.carrier_sync_checkpoint(singleton) VALUES(true);
CREATE TABLE fugates.carrier_sync_request (
 id uuid PRIMARY KEY, bp varchar(40) NOT NULL, authenticated_sid varchar(180) NOT NULL,
 requested_utc timestamptz NOT NULL, status varchar(40) NOT NULL DEFAULT 'PENDING', result_code varchar(100)
);
CREATE INDEX carrier_sync_request_pending ON fugates.carrier_sync_request(requested_utc,id) WHERE status='PENDING';
-- Only newly created objects are revoked; existing approved features are not changed.
REVOKE ALL ON fugates.carrier_sync_cycle, fugates.carrier_sync_state, fugates.carrier_sync_attempt, fugates.carrier_sync_step, fugates.carrier_sync_checkpoint, fugates.carrier_sync_request FROM PUBLIC;
REVOKE ALL ON SEQUENCE fugates.carrier_sync_step_id_seq FROM PUBLIC;
DO $carrier_minimal_grants$
DECLARE app_role text := current_setting('fugates.carrier_app_role'); target regclass;
BEGIN
 FOR target IN SELECT unnest(ARRAY['fugates.carrier_sync_cycle'::regclass,'fugates.carrier_sync_state'::regclass,'fugates.carrier_sync_attempt'::regclass,'fugates.carrier_sync_step'::regclass,'fugates.carrier_sync_checkpoint'::regclass,'fugates.carrier_sync_request'::regclass]) LOOP
  IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_roles r ON r.oid=c.relowner WHERE c.oid=target AND r.rolname=app_role) THEN
   RAISE EXCEPTION 'CARRIER_APP_ROLE_MUST_NOT_OWN_TABLES';
  END IF;
  EXECUTE format('REVOKE ALL ON TABLE %s FROM %I',target,app_role);
 END LOOP;
 EXECUTE format('REVOKE ALL ON SEQUENCE fugates.carrier_sync_step_id_seq FROM %I',app_role);
 EXECUTE format('GRANT USAGE ON SCHEMA fugates TO %I',app_role);
 EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE fugates.carrier_sync_cycle, fugates.carrier_sync_state, fugates.carrier_sync_attempt, fugates.carrier_sync_step, fugates.carrier_sync_checkpoint, fugates.carrier_sync_request TO %I',app_role);
 -- GENERATED ALWAYS AS IDENTITY inserts do not invoke nextval() with sequence ACL checks.
 -- The application does not call nextval/currval/setval or read the sequence directly.
 -- No GRANT USAGE ON SEQUENCE is necessary for the approved queries/identity column.
 IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid='fugates.carrier_sync_step'::regclass AND attname='id' AND attidentity='a') THEN
  RAISE EXCEPTION 'CARRIER_STEP_IDENTITY_REQUIRED';
 END IF;
 FOR target IN SELECT unnest(ARRAY['fugates.carrier_sync_cycle'::regclass,'fugates.carrier_sync_state'::regclass,'fugates.carrier_sync_attempt'::regclass,'fugates.carrier_sync_step'::regclass,'fugates.carrier_sync_checkpoint'::regclass,'fugates.carrier_sync_request'::regclass]) LOOP
  IF NOT has_table_privilege(app_role,target,'SELECT') OR NOT has_table_privilege(app_role,target,'INSERT') OR NOT has_table_privilege(app_role,target,'UPDATE')
     OR has_table_privilege(app_role,target,'DELETE') OR has_table_privilege(app_role,target,'TRUNCATE') OR has_table_privilege(app_role,target,'REFERENCES') OR has_table_privilege(app_role,target,'TRIGGER') THEN
   RAISE EXCEPTION 'CARRIER_APP_ROLE_EFFECTIVE_GRANTS_INVALID';
  END IF;
 END LOOP;
 IF has_sequence_privilege(app_role,'fugates.carrier_sync_step_id_seq','USAGE') OR has_sequence_privilege(app_role,'fugates.carrier_sync_step_id_seq','SELECT') OR has_sequence_privilege(app_role,'fugates.carrier_sync_step_id_seq','UPDATE') THEN
  RAISE EXCEPTION 'CARRIER_APP_ROLE_EXCESSIVE_SEQUENCE_PRIVILEGES';
 END IF;
END; $carrier_minimal_grants$;
COMMIT;