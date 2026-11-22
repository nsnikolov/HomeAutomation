CREATE TABLE IF NOT EXISTS ewelink_account (
    id TINYINT NOT NULL PRIMARY KEY,
    owner_id VARCHAR(64) NOT NULL,
    token_envelope VARBINARY(16384) NULL,
    status VARCHAR(32) NOT NULL,
    last_success_utc DATETIME(6) NULL,
    next_sync_utc DATETIME(6) NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ewelink_devices (
    public_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    owner_id VARCHAR(64) NOT NULL,
    external_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    property_id BIGINT NULL,
    room_id BIGINT NULL,
    snapshot JSON NOT NULL,
    UNIQUE KEY ux_ewelink_identity (owner_id, external_id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    FOREIGN KEY (property_id, room_id) REFERENCES rooms(property_id, id)
) ENGINE=InnoDB;
