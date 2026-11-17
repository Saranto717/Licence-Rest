ALTER TABLE cloud_license_installations
    ADD COLUMN client_token_sha256 bytea
    CHECK (client_token_sha256 IS NULL OR octet_length(client_token_sha256) = 32);
