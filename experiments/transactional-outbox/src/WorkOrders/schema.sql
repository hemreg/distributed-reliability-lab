-- E01 Lost Event schema. Applied idempotently at service startup
-- (inside a transaction guarded by an advisory lock, see SchemaInitializer).

CREATE TABLE IF NOT EXISTS work_orders (
    id           uuid        PRIMARY KEY,
    title        text        NOT NULL,
    operation_id text        NOT NULL,
    -- Which write path created the row. Lets the evidence queries tell the
    -- unsafe baseline apart from the outbox mitigation.
    write_mode   text        NOT NULL CHECK (write_mode IN ('naive', 'outbox')),
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- Transactional outbox. A row is inserted in the same transaction as the
-- business change and records the *intent* to publish an event.
CREATE TABLE IF NOT EXISTS outbox_messages (
    event_id     uuid        PRIMARY KEY,          -- stable; reused on every publish attempt
    event_type   text        NOT NULL,
    aggregate_id uuid        NOT NULL,             -- work_orders.id (no FK: an outbox is usually generic)
    operation_id text        NOT NULL,             -- correlation id of the originating request
    payload      jsonb       NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL                  -- NULL = pending
);

CREATE INDEX IF NOT EXISTS outbox_messages_pending_idx
    ON outbox_messages (created_at, event_id)
    WHERE published_at IS NULL;

-- Outbox rows are immutable: only published_at may change, and only once
-- (NULL -> timestamp). Deleting old published rows (retention) is allowed.
CREATE OR REPLACE FUNCTION outbox_messages_guard_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.event_id, NEW.event_type, NEW.aggregate_id, NEW.operation_id, NEW.payload, NEW.created_at)
       IS DISTINCT FROM
       (OLD.event_id, OLD.event_type, OLD.aggregate_id, OLD.operation_id, OLD.payload, OLD.created_at) THEN
        RAISE EXCEPTION 'outbox_messages rows are immutable (event_id=%)', OLD.event_id;
    END IF;
    IF OLD.published_at IS NOT NULL AND NEW.published_at IS DISTINCT FROM OLD.published_at THEN
        RAISE EXCEPTION 'outbox_messages.published_at can only be set once (event_id=%)', OLD.event_id;
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER outbox_messages_immutable
    BEFORE UPDATE ON outbox_messages
    FOR EACH ROW EXECUTE FUNCTION outbox_messages_guard_immutable();
