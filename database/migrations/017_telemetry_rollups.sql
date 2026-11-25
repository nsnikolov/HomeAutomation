CREATE TABLE IF NOT EXISTS telemetry_rollups_hourly (
    series_id BIGINT NOT NULL,
    bucket_start_utc DATETIME(6) NOT NULL,
    sample_count INT UNSIGNED NOT NULL,
    covered_seconds DOUBLE NOT NULL,
    min_value DOUBLE NULL,
    max_value DOUBLE NULL,
    time_weighted_avg DOUBLE NULL,
    value_sum DOUBLE NULL,
    first_observed_utc DATETIME(6) NULL,
    last_observed_utc DATETIME(6) NULL,
    last_numeric_value DOUBLE NULL,
    on_seconds DOUBLE NULL,
    transition_count INT UNSIGNED NOT NULL DEFAULT 0,
    measured_wh DOUBLE NULL,
    estimated_positive_wh DOUBLE NULL,
    estimated_negative_wh DOUBLE NULL,
    quality_flags SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    computed_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (series_id, bucket_start_utc),
    CONSTRAINT fk_telemetry_rollup_series FOREIGN KEY (series_id) REFERENCES telemetry_series(id),
    CONSTRAINT ck_telemetry_rollup_coverage CHECK (covered_seconds >= 0 AND covered_seconds <= 3600.001)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS telemetry_rollup_state (
    series_id BIGINT NOT NULL PRIMARY KEY,
    rolled_from_utc DATETIME(6) NOT NULL,
    rolled_to_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    CONSTRAINT fk_telemetry_rollup_state_series FOREIGN KEY (series_id) REFERENCES telemetry_series(id),
    CONSTRAINT ck_telemetry_rollup_state_range CHECK (rolled_from_utc <= rolled_to_utc)
) ENGINE=InnoDB;
