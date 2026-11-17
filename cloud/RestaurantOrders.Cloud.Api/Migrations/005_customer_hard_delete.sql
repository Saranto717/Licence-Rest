ALTER TABLE cloud_license_audit
    ADD COLUMN customer_id uuid NULL;

UPDATE cloud_license_audit audit
SET customer_id = license.customer_id
FROM cloud_licenses license
WHERE audit.license_id = license.id
  AND audit.customer_id IS NULL;

UPDATE cloud_license_audit audit
SET customer_id = license.customer_id
FROM cloud_license_installations installation
JOIN cloud_licenses license ON license.id = installation.license_id
WHERE audit.installation_id = installation.installation_id
  AND audit.customer_id IS NULL;

ALTER TABLE cloud_license_audit
    ADD CONSTRAINT fk_cloud_license_audit_customer
        FOREIGN KEY (customer_id) REFERENCES cloud_license_customers(id) ON DELETE CASCADE;

CREATE INDEX ix_cloud_license_audit_customer
    ON cloud_license_audit(customer_id, created_at_utc DESC);
