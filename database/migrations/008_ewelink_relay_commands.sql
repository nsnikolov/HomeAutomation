CREATE TABLE IF NOT EXISTS ewelink_relay_commands (
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    device_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    property_id BIGINT NOT NULL,
    actor_id VARCHAR(64) NOT NULL,
    desired_on BOOLEAN NOT NULL,
    requested_at_utc DATETIME(6) NOT NULL,
    status VARCHAR(16) NOT NULL,
    resolved_at_utc DATETIME(6) NULL,
    active_device CHAR(32) CHARACTER SET ascii COLLATE ascii_bin
        GENERATED ALWAYS AS (IF(resolved_at_utc IS NULL, device_id, NULL)) STORED,
    UNIQUE KEY ux_ewelink_active_relay_command (active_device),
    KEY ix_ewelink_relay_audit (device_id, property_id, requested_at_utc, id),
    FOREIGN KEY (device_id) REFERENCES ewelink_devices(public_id)
) ENGINE=InnoDB;
