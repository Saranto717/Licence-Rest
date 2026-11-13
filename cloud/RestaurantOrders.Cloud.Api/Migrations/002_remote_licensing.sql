CREATE TABLE cloud_licenses (
    id uuid PRIMARY KEY,
    customer_name varchar(160) NOT NULL,
    vat_number varchar(32) NOT NULL DEFAULT '',
    status varchar(16) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    expires_at_utc timestamp with time zone NULL,
    max_installations integer NOT NULL DEFAULT 1 CHECK (max_installations BETWEEN 1 AND 100),
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
);

CREATE TABLE cloud_license_installations (
    installation_id uuid PRIMARY KEY,
    machine_name varchar(120) NOT NULL,
    mac_address_sha256 bytea NOT NULL,
    first_seen_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    last_seen_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    license_id uuid NULL REFERENCES cloud_licenses(id) ON DELETE RESTRICT,
    is_assigned boolean NOT NULL DEFAULT false
);

CREATE INDEX ix_cloud_license_installations_license
    ON cloud_license_installations(license_id, is_assigned);
CREATE INDEX ix_cloud_license_installations_last_seen
    ON cloud_license_installations(last_seen_at_utc DESC);

CREATE TABLE cloud_license_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    license_id uuid NULL REFERENCES cloud_licenses(id) ON DELETE RESTRICT,
    installation_id uuid NULL REFERENCES cloud_license_installations(installation_id) ON DELETE RESTRICT,
    action varchar(32) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now()
);
