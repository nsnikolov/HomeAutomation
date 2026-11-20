CREATE TABLE IF NOT EXISTS properties (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    site_key VARCHAR(64) NOT NULL UNIQUE,
    name VARCHAR(128) NOT NULL,
    time_zone VARCHAR(64) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS rooms (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    room_key VARCHAR(64) NOT NULL,
    name VARCHAR(128) NOT NULL,
    UNIQUE KEY ux_room_key (property_id, room_key),
    UNIQUE KEY ux_room_scope (property_id, id),
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS installations (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    installation_key VARCHAR(64) NOT NULL,
    name VARCHAR(128) NOT NULL,
    UNIQUE KEY ux_installation_key (property_id, installation_key),
    UNIQUE KEY ux_installation_scope (property_id, id),
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS devices (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    room_id BIGINT NULL,
    inventory_key VARCHAR(128) NOT NULL,
    name VARCHAR(128) NOT NULL,
    kind VARCHAR(32) NOT NULL,
    manufacturer VARCHAR(64) NOT NULL,
    model VARCHAR(96) NOT NULL,
    UNIQUE KEY ux_device_key (property_id, inventory_key),
    UNIQUE KEY ux_device_scope (property_id, id),
    KEY ix_device_room (property_id, room_id, kind, id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    FOREIGN KEY (property_id, room_id) REFERENCES rooms(property_id, id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS installation_devices (
    property_id BIGINT NOT NULL,
    installation_id BIGINT NOT NULL,
    device_id BIGINT NOT NULL,
    role_code VARCHAR(32) NOT NULL,
    PRIMARY KEY (installation_id, device_id),
    FOREIGN KEY (property_id, installation_id) REFERENCES installations(property_id, id),
    FOREIGN KEY (property_id, device_id) REFERENCES devices(property_id, id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS device_endpoints (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    device_id BIGINT NOT NULL,
    endpoint_key VARCHAR(64) COLLATE utf8mb4_bin NOT NULL,
    name VARCHAR(128) NOT NULL,
    UNIQUE KEY ux_endpoint_key (device_id, endpoint_key),
    UNIQUE KEY ux_endpoint_property (property_id, id),
    UNIQUE KEY ux_endpoint_device (device_id, id),
    FOREIGN KEY (property_id, device_id) REFERENCES devices(property_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS device_capabilities (
    endpoint_id BIGINT NOT NULL,
    capability_code VARCHAR(48) NOT NULL,
    readable BOOLEAN NOT NULL DEFAULT TRUE,
    writable BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (endpoint_id, capability_code),
    FOREIGN KEY (endpoint_id) REFERENCES device_endpoints(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS integration_connections (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    provider_code VARCHAR(32) NOT NULL,
    UNIQUE KEY ux_connection_kind (property_id, provider_code),
    UNIQUE KEY ux_connection_scope (property_id, id),
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS provider_bindings (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    connection_id BIGINT NOT NULL,
    device_id BIGINT NOT NULL,
    external_id VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
    UNIQUE KEY ux_external_id (connection_id, external_id),
    UNIQUE KEY ux_binding_device (device_id, id),
    FOREIGN KEY (property_id, connection_id) REFERENCES integration_connections(property_id, id),
    FOREIGN KEY (property_id, device_id) REFERENCES devices(property_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS provider_endpoint_bindings (
    binding_id BIGINT NOT NULL,
    device_id BIGINT NOT NULL,
    endpoint_id BIGINT NOT NULL,
    external_endpoint_key VARCHAR(64) COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (binding_id, external_endpoint_key),
    FOREIGN KEY (device_id, binding_id) REFERENCES provider_bindings(device_id, id),
    FOREIGN KEY (device_id, endpoint_id) REFERENCES device_endpoints(device_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS device_state (
    device_id BIGINT NOT NULL PRIMARY KEY,
    connectivity VARCHAR(16) NOT NULL,
    observed_at_utc DATETIME(6) NOT NULL,
    last_contact_utc DATETIME(6) NULL,
    fault_code VARCHAR(64) NULL,
    FOREIGN KEY (device_id) REFERENCES devices(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS device_alerts (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    device_id BIGINT NOT NULL,
    occurred_at_utc DATETIME(6) NOT NULL,
    alert_code VARCHAR(64) NOT NULL,
    UNIQUE KEY ux_device_alert (device_id, occurred_at_utc, alert_code),
    FOREIGN KEY (device_id) REFERENCES devices(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS telemetry_series (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    endpoint_id BIGINT NULL,
    installation_id BIGINT NULL,
    source_key VARCHAR(64) NOT NULL,
    metric VARCHAR(48) NOT NULL,
    UNIQUE KEY ux_endpoint_series (endpoint_id, metric, source_key),
    UNIQUE KEY ux_installation_series (installation_id, metric, source_key),
    KEY ix_series_property (property_id, endpoint_id, installation_id),
    FOREIGN KEY (property_id, endpoint_id) REFERENCES device_endpoints(property_id, id),
    FOREIGN KEY (property_id, installation_id) REFERENCES installations(property_id, id),
    CHECK ((endpoint_id IS NULL) <> (installation_id IS NULL))
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS telemetry_samples (
    series_id BIGINT NOT NULL,
    observed_at_utc DATETIME(6) NOT NULL,
    received_at_utc DATETIME(6) NOT NULL,
    numeric_value DOUBLE NULL,
    boolean_value BOOLEAN NULL,
    status_value VARCHAR(32) NULL,
    PRIMARY KEY (series_id, observed_at_utc),
    FOREIGN KEY (series_id) REFERENCES telemetry_series(id),
    CHECK ((numeric_value IS NOT NULL) + (boolean_value IS NOT NULL) + (status_value IS NOT NULL) = 1)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS latest_readings (
    series_id BIGINT NOT NULL PRIMARY KEY,
    observed_at_utc DATETIME(6) NOT NULL,
    received_at_utc DATETIME(6) NOT NULL,
    numeric_value DOUBLE NULL,
    boolean_value BOOLEAN NULL,
    status_value VARCHAR(32) NULL,
    FOREIGN KEY (series_id) REFERENCES telemetry_series(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS identity_users (
    id CHAR(36) NOT NULL PRIMARY KEY,
    email VARCHAR(254) NOT NULL,
    normalized_email VARCHAR(254) NOT NULL UNIQUE,
    password_hash VARCHAR(512) NOT NULL,
    security_stamp VARCHAR(64) NOT NULL,
    concurrency_stamp VARCHAR(64) NOT NULL,
    failed_count INT NOT NULL DEFAULT 0,
    lockout_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    lockout_end_utc DATETIME(6) NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS property_memberships (
    property_id BIGINT NOT NULL,
    user_id CHAR(36) NOT NULL,
    role_code VARCHAR(16) NOT NULL,
    PRIMARY KEY (property_id, user_id),
    KEY ix_membership_user (user_id, property_id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    FOREIGN KEY (user_id) REFERENCES identity_users(id)
) ENGINE=InnoDB;
