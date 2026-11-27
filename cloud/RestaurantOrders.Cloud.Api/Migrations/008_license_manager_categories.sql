CREATE TABLE cloud_license_categories (
    id uuid PRIMARY KEY,
    name varchar(80) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    updated_at_utc timestamp with time zone NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX ix_cloud_license_categories_name
    ON cloud_license_categories (lower(name));

ALTER TABLE cloud_license_customers
    ADD COLUMN category_id uuid NULL
        REFERENCES cloud_license_categories(id) ON DELETE SET NULL;

CREATE INDEX ix_cloud_license_customers_category
    ON cloud_license_customers(category_id, is_active, shop_name);
