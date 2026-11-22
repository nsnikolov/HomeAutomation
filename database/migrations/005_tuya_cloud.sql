CREATE TABLE IF NOT EXISTS tuya_sync (
    project_scope CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    status VARCHAR(32) NOT NULL,
    provider_code INT NULL,
    last_success_utc DATETIME(6) NULL,
    next_sync_utc DATETIME(6) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS tuya_devices (
    public_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    project_scope CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    external_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    property_id BIGINT NULL,
    room_id BIGINT NULL,
    snapshot JSON NOT NULL,
    UNIQUE KEY ux_tuya_identity (project_scope, external_id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    FOREIGN KEY (property_id, room_id) REFERENCES rooms(property_id, id)
) ENGINE=InnoDB;
