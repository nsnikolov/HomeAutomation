CREATE TABLE IF NOT EXISTS ewelink_request_days (
    region CHAR(2) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    day_utc DATE NOT NULL,
    requests INT UNSIGNED NOT NULL,
    PRIMARY KEY (region, day_utc)
) ENGINE=InnoDB;

ALTER TABLE ewelink_account ADD COLUMN last_attempt_utc DATETIME(6) NULL;
