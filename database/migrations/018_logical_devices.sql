CREATE TABLE IF NOT EXISTS logical_devices (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    name VARCHAR(128) NOT NULL,
    kind VARCHAR(32) NOT NULL,
    room_id BIGINT NULL,
    description VARCHAR(256) NULL,
    origin_device_id BIGINT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    archived_at_utc DATETIME(6) NULL,
    UNIQUE KEY ux_logical_device_scope (property_id, id),
    UNIQUE KEY ux_logical_device_origin (property_id, origin_device_id),
    KEY ix_logical_device_list (property_id, archived_at_utc, name),
    CONSTRAINT fk_logical_device_property FOREIGN KEY (property_id) REFERENCES properties(id),
    CONSTRAINT fk_logical_device_room FOREIGN KEY (property_id, room_id) REFERENCES rooms(property_id, id),
    CONSTRAINT fk_logical_device_origin FOREIGN KEY (property_id, origin_device_id) REFERENCES devices(property_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS logical_device_assignments (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    logical_device_id BIGINT NOT NULL,
    channel_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_id BIGINT NOT NULL,
    endpoint_id BIGINT NULL,
    valid_from_utc DATETIME(6) NOT NULL,
    valid_to_utc DATETIME(6) NULL,
    assigned_by VARCHAR(64) NOT NULL,
    assigned_at_utc DATETIME(6) NOT NULL,
    reason VARCHAR(256) NULL,
    open_slot TINYINT AS (IF(valid_to_utc IS NULL, 1, NULL)) STORED,
    UNIQUE KEY ux_logical_assignment_open (logical_device_id, channel_key, open_slot),
    KEY ix_logical_assignment_history (logical_device_id, channel_key, valid_from_utc),
    KEY ix_logical_assignment_device (device_id, valid_from_utc),
    CONSTRAINT fk_logical_assignment_device_scope FOREIGN KEY (property_id, logical_device_id) REFERENCES logical_devices(property_id, id),
    CONSTRAINT fk_logical_assignment_physical FOREIGN KEY (property_id, device_id) REFERENCES devices(property_id, id),
    CONSTRAINT fk_logical_assignment_endpoint FOREIGN KEY (device_id, endpoint_id) REFERENCES device_endpoints(device_id, id),
    CONSTRAINT ck_logical_assignment_interval CHECK (valid_to_utc IS NULL OR valid_to_utc > valid_from_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS logical_device_audit (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    logical_device_id BIGINT NOT NULL,
    occurred_at_utc DATETIME(6) NOT NULL,
    actor VARCHAR(64) NOT NULL,
    action VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    channel_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    old_device_id BIGINT NULL,
    new_device_id BIGINT NULL,
    effective_at_utc DATETIME(6) NULL,
    reason VARCHAR(256) NULL,
    KEY ix_logical_audit_device (property_id, logical_device_id, occurred_at_utc, id),
    CONSTRAINT fk_logical_audit_device FOREIGN KEY (property_id, logical_device_id) REFERENCES logical_devices(property_id, id),
    CONSTRAINT ck_logical_audit_action CHECK (action IN ('created', 'initialized', 'updated', 'assigned', 'replaced', 'corrected', 'archived'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
