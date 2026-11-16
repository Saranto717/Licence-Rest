ALTER TABLE cloud_license_installations
    ADD COLUMN rebind_pending boolean NOT NULL DEFAULT false;
