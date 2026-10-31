-- REVIEW ONLY. NOT EXECUTED. Target identity, owner and app role must be approved first.
-- Additive inbox only; no modification of existing Guardian correlations or motor queues.
-- No roles, grants, data inserts, fake correlation or worker activation.
BEGIN;
CREATE TABLE fugates.toledo_pesagem_recebida (
    recibo_id uuid PRIMARY KEY,
    origem text NOT NULL CHECK (origem = 'TOLEDO'),
    centro text NOT NULL CHECK (centro ~ '^[0-9]+$' AND length(centro) <= 128),
    id_pesagem text NOT NULL CHECK (id_pesagem ~ '^[0-9]+$' AND length(id_pesagem) <= 128),
    data_pesagem_local timestamp without time zone NOT NULL,
    documento_json jsonb NOT NULL CHECK (jsonb_typeof(documento_json) = 'object'),
    fingerprint_sha256 text NOT NULL CHECK (fingerprint_sha256 ~ '^[0-9A-F]{64}$'),
    estado text NOT NULL CHECK (estado = 'RECEIVED_PENDING_CORRELATION'),
    recebido_em timestamp with time zone NOT NULL,
    CONSTRAINT uq_toledo_pesagem_recebida_identidade UNIQUE (origem, centro, id_pesagem)
);
COMMIT;
