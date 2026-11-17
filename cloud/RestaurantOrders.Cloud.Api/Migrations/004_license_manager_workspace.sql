CREATE TABLE cloud_license_customers (
    id uuid PRIMARY KEY,
    store_identifier varchar(80) NOT NULL UNIQUE,
    shop_name varchar(160) NOT NULL,
    reseller_name varchar(160) NOT NULL DEFAULT '',
    vat_number varchar(32) NOT NULL DEFAULT '',
    is_active boolean NOT NULL DEFAULT true,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    updated_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    deleted_at_utc timestamp with time zone NULL
);

CREATE TABLE cloud_license_editions (
    id uuid PRIMARY KEY,
    name varchar(80) NOT NULL UNIQUE,
    max_installations integer NOT NULL CHECK (max_installations BETWEEN 1 AND 100),
    default_duration_months integer NOT NULL CHECK (default_duration_months BETWEEN 1 AND 120),
    is_active boolean NOT NULL DEFAULT true,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
);

INSERT INTO cloud_license_editions (id, name, max_installations, default_duration_months)
VALUES ('00000000-0000-0000-0000-000000000001', 'Βασική', 1, 12);

ALTER TABLE cloud_licenses
    ADD COLUMN customer_id uuid NULL,
    ADD COLUMN edition_id uuid NULL REFERENCES cloud_license_editions(id) ON DELETE RESTRICT;

INSERT INTO cloud_license_customers (id, store_identifier, shop_name, reseller_name, vat_number)
SELECT id,
       'LEGACY-' || replace(id::text, '-', ''),
       customer_name,
       '',
       vat_number
FROM cloud_licenses;

UPDATE cloud_licenses SET customer_id = id;

ALTER TABLE cloud_licenses
    ALTER COLUMN customer_id SET NOT NULL,
    ADD CONSTRAINT fk_cloud_licenses_customer
        FOREIGN KEY (customer_id) REFERENCES cloud_license_customers(id) ON DELETE RESTRICT;

CREATE INDEX ix_cloud_licenses_customer
    ON cloud_licenses(customer_id, created_at_utc DESC);

CREATE INDEX ix_cloud_license_customers_active
    ON cloud_license_customers(is_active, shop_name);
