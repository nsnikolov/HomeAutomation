CREATE TABLE IF NOT EXISTS daikin_account (
    id TINYINT NOT NULL PRIMARY KEY,
    owner_id VARCHAR(64) NOT NULL,
    token_envelope VARBINARY(16384) NULL,
    status VARCHAR(32) NOT NULL,
    last_success_utc DATETIME(6) NULL,
    retry_after_utc DATETIME(6) NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS daikin_devices (
    public_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    owner_id VARCHAR(64) NOT NULL,
    external_id VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
    property_id BIGINT NULL,
    room_id BIGINT NULL,
    snapshot JSON NULL,
    UNIQUE KEY ux_daikin_identity (owner_id, external_id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    FOREIGN KEY (property_id, room_id) REFERENCES rooms(property_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS daikin_requests (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    requested_utc DATETIME(6) NOT NULL,
    KEY ix_daikin_window (requested_utc)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS daikin_commands (
    id CHAR(32) PRIMARY KEY,
    device_key CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    property_id BIGINT NOT NULL,
    actor_id VARCHAR(64) NOT NULL,
    operation VARCHAR(32) NOT NULL,
    desired VARCHAR(128) NOT NULL,
    status VARCHAR(32) NOT NULL,
    requested_utc DATETIME(6) NOT NULL,
    updated_utc DATETIME(6) NOT NULL,
    KEY ix_daikin_command_device (device_key, requested_utc),
    FOREIGN KEY (device_key) REFERENCES daikin_devices(public_id),
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB;
