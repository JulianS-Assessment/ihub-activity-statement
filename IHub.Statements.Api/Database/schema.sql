CREATE TABLE IF NOT EXISTS intermediary_activity (
    source          text          NOT NULL CHECK (length(btrim(source)) BETWEEN 1 AND 64),
    activity_id     text          NOT NULL CHECK (length(btrim(activity_id)) BETWEEN 1 AND 64),
    intermediary_id text          NOT NULL CHECK (length(btrim(intermediary_id)) BETWEEN 1 AND 64),
    amount          numeric(19,4) NOT NULL CHECK (amount <> 0),
    currency        char(3)       NOT NULL CHECK (currency = 'ZAR'),
    occurred_at     timestamptz   NOT NULL,
    received_at     timestamptz   NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (source, activity_id)
);

CREATE INDEX IF NOT EXISTS intermediary_activity_by_intermediary_and_time
    ON intermediary_activity (intermediary_id, occurred_at);
