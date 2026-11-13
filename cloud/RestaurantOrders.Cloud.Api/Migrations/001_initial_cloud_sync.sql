CREATE TABLE cloud_stores (
    id uuid PRIMARY KEY,
    name varchar(120) NOT NULL,
    api_key_sha256 bytea NOT NULL UNIQUE,
    is_active boolean NOT NULL DEFAULT TRUE,
    created_at_utc timestamp with time zone NOT NULL DEFAULT now()
);

CREATE TABLE cloud_sync_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id uuid NOT NULL REFERENCES cloud_stores(id) ON DELETE RESTRICT,
    event_id uuid NOT NULL,
    entity_type varchar(80) NOT NULL,
    entity_id varchar(160) NOT NULL,
    event_type varchar(40) NOT NULL,
    schema_version integer NOT NULL CHECK (schema_version BETWEEN 1 AND 1000),
    occurred_at_utc timestamp with time zone NOT NULL,
    received_at_utc timestamp with time zone NOT NULL DEFAULT now(),
    payload jsonb NOT NULL,
    CONSTRAINT uq_cloud_sync_events_store_event UNIQUE (store_id, event_id)
);

CREATE INDEX ix_cloud_sync_events_store_received
    ON cloud_sync_events(store_id, received_at_utc, id);
