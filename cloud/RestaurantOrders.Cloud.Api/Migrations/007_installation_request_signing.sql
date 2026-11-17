ALTER TABLE cloud_license_installations
    ADD COLUMN client_public_key bytea
    CHECK (client_public_key IS NULL OR octet_length(client_public_key) BETWEEN 70 AND 200);

CREATE TABLE cloud_license_request_nonces (
    installation_id uuid NOT NULL REFERENCES cloud_license_installations (installation_id) ON DELETE CASCADE,
    nonce_sha256 bytea NOT NULL CHECK (octet_length(nonce_sha256) = 32),
    received_at_utc timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (installation_id, nonce_sha256)
);

CREATE INDEX cloud_license_request_nonces_received_at_idx
    ON cloud_license_request_nonces (received_at_utc);
