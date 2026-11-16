ALTER TABLE cloud_licenses
    ADD COLUMN comments text NOT NULL DEFAULT '';

ALTER TABLE cloud_licenses
    DROP CONSTRAINT IF EXISTS cloud_licenses_status_check;

ALTER TABLE cloud_licenses
    ADD CONSTRAINT cloud_licenses_status_check
    CHECK (status IN ('active', 'revoked', 'deleted'));
