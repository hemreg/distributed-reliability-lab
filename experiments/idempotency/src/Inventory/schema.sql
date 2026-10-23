-- E02 consumer schema (inventory). Applied idempotently at service startup.
--
-- Both consumers (naive and idempotent) use these tables side by side; the `consumer` column
-- keeps their data apart, so each one has its own stock level for the same part.

-- Business state: how many units of a part are on the shelf.
CREATE TABLE IF NOT EXISTS stock_levels (
    consumer        text NOT NULL,
    part_no         text NOT NULL,
    initial_on_hand int  NOT NULL,                 -- seeded once; lets the evidence queries compute the correct level
    on_hand         int  NOT NULL,                 -- no CHECK (on_hand >= 0): the lab does not model "out of stock"
    PRIMARY KEY (consumer, part_no)
);

-- Business effect: one row per reservation made for a work order.
-- Deliberately NO unique constraint on (consumer, event_id) or (consumer, work_order_id):
-- deduplication is the job of processed_messages, so the naive baseline can show what a
-- duplicate does. (A natural-key constraint is a valid complementary safeguard; see README.)
CREATE TABLE IF NOT EXISTS part_reservations (
    reservation_id uuid        PRIMARY KEY,
    consumer       text        NOT NULL,
    event_id       uuid        NOT NULL,
    work_order_id  uuid        NOT NULL,
    operation_id   text        NOT NULL,
    part_no        text        NOT NULL,
    quantity       int         NOT NULL,
    reserved_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS part_reservations_event_idx ON part_reservations (consumer, event_id);

-- Idempotent consumer only: one row per event this consumer has applied. The primary key
-- (consumer, event_id) decides whether a delivery is a duplicate. The row is inserted in the
-- SAME transaction as the reservation and the stock update, so either all three commit or none.
CREATE TABLE IF NOT EXISTS processed_messages (
    consumer     text        NOT NULL,
    event_id     uuid        NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer, event_id)
);

-- LAB INSTRUMENTATION, not business state: one row per delivery the consumer received, written
-- in its own autocommit statement before processing. It records what the broker delivered
-- (including the redelivered flag) so the experiment can count deliveries per event_id.
-- A real consumer would emit a log line or a metric instead.
CREATE TABLE IF NOT EXISTS consumer_deliveries (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    consumer     text        NOT NULL,
    instance     text        NOT NULL,             -- random id per process start: shows deliveries across restarts
    event_id     uuid        NULL,                 -- NULL if the payload could not be read
    message_id   text        NULL,
    operation_id text        NULL,
    delivery_tag bigint      NOT NULL,
    redelivered  boolean     NOT NULL,
    received_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS consumer_deliveries_event_idx ON consumer_deliveries (consumer, event_id);
