-- Event rules, incidents and notifications. Additive: no existing table changes, no DELETE grant needed
-- (rules are soft-deleted so incidents and notifications keep their history).

CREATE TABLE IF NOT EXISTS alert_rules (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(500) NULL,
    enabled BOOLEAN NOT NULL,
    severity VARCHAR(16) NOT NULL,
    correlation_key VARCHAR(80) NULL,
    incident_title VARCHAR(120) NULL,
    definition JSON NOT NULL,
    activation_seconds INT NOT NULL,
    recovery_seconds INT NOT NULL,
    notification_policy JSON NOT NULL,
    template_code VARCHAR(48) NULL,
    -- Device ids the definition reads, for device filters and moved-device warnings.
    device_ids JSON NOT NULL,
    revision INT NOT NULL,
    created_by VARCHAR(64) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    updated_by VARCHAR(64) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    deleted_at_utc DATETIME(6) NULL,
    KEY ix_alert_rules_property (property_id, deleted_at_utc, enabled, id),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    CHECK (severity IN ('info', 'warning', 'critical')),
    CHECK (activation_seconds BETWEEN 0 AND 86400 AND recovery_seconds BETWEEN 0 AND 86400)
) ENGINE=InnoDB;

-- Every saved change, enable/disable and deletion as a full snapshot.
CREATE TABLE IF NOT EXISTS alert_rule_revisions (
    rule_id BIGINT NOT NULL,
    revision INT NOT NULL,
    action VARCHAR(16) NOT NULL,
    snapshot JSON NOT NULL,
    changed_by VARCHAR(64) NOT NULL,
    changed_at_utc DATETIME(6) NOT NULL,
    PRIMARY KEY (rule_id, revision),
    FOREIGN KEY (rule_id) REFERENCES alert_rules(id)
) ENGINE=InnoDB;

-- Persisted detection state, so a restart resumes an ongoing condition instead of opening a new incident.
CREATE TABLE IF NOT EXISTS alert_rule_state (
    rule_id BIGINT NOT NULL PRIMARY KEY,
    property_id BIGINT NOT NULL,
    revision INT NOT NULL,
    phase VARCHAR(16) NOT NULL,
    truth VARCHAR(8) NOT NULL,
    true_since_utc DATETIME(6) NULL,
    false_since_utc DATETIME(6) NULL,
    active_since_utc DATETIME(6) NULL,
    inputs_observed_at_utc DATETIME(6) NULL,
    last_evaluated_at_utc DATETIME(6) NOT NULL,
    last_triggered_at_utc DATETIME(6) NULL,
    evidence JSON NULL,
    KEY ix_alert_rule_state_property (property_id),
    FOREIGN KEY (rule_id) REFERENCES alert_rules(id)
) ENGINE=InnoDB;

-- One row per evaluated property; evaluations lock it so concurrent workers serialize per property.
CREATE TABLE IF NOT EXISTS alert_evaluation_locks (
    property_id BIGINT NOT NULL PRIMARY KEY,
    last_evaluated_at_utc DATETIME(6) NULL,
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS alert_incidents (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    correlation_key VARCHAR(96) NOT NULL,
    title VARCHAR(120) NOT NULL,
    status VARCHAR(16) NOT NULL,
    severity VARCHAR(16) NOT NULL,
    original_severity VARCHAR(16) NOT NULL,
    max_severity VARCHAR(16) NOT NULL,
    started_at_utc DATETIME(6) NOT NULL,
    last_changed_at_utc DATETIME(6) NOT NULL,
    resolved_at_utc DATETIME(6) NULL,
    resolution VARCHAR(24) NULL,
    acknowledged_at_utc DATETIME(6) NULL,
    acknowledged_by VARCHAR(64) NULL,
    last_notified_at_utc DATETIME(6) NULL,
    -- At most one active incident per property and correlation family, enforced by the database.
    active_key VARCHAR(96) GENERATED ALWAYS AS (IF(status = 'active', correlation_key, NULL)) STORED,
    UNIQUE KEY ux_alert_incident_active (property_id, active_key),
    KEY ix_alert_incident_property (property_id, status, started_at_utc),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    CHECK (status IN ('active', 'resolved'))
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS alert_incident_rules (
    incident_id BIGINT NOT NULL,
    rule_id BIGINT NOT NULL,
    rule_revision INT NOT NULL,
    rule_name VARCHAR(120) NOT NULL,
    severity VARCHAR(16) NOT NULL,
    active BOOLEAN NOT NULL,
    first_active_at_utc DATETIME(6) NOT NULL,
    last_active_at_utc DATETIME(6) NOT NULL,
    cleared_at_utc DATETIME(6) NULL,
    cleared_reason VARCHAR(24) NULL,
    PRIMARY KEY (incident_id, rule_id),
    KEY ix_alert_incident_rule (rule_id, incident_id),
    FOREIGN KEY (incident_id) REFERENCES alert_incidents(id),
    FOREIGN KEY (rule_id) REFERENCES alert_rules(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS alert_incident_events (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    incident_id BIGINT NOT NULL,
    property_id BIGINT NOT NULL,
    occurred_at_utc DATETIME(6) NOT NULL,
    observed_at_utc DATETIME(6) NULL,
    kind VARCHAR(24) NOT NULL,
    rule_id BIGINT NULL,
    rule_revision INT NULL,
    from_severity VARCHAR(16) NULL,
    to_severity VARCHAR(16) NULL,
    actor VARCHAR(64) NULL,
    dedupe_key VARCHAR(64) NULL,
    detail JSON NULL,
    UNIQUE KEY ux_alert_incident_event_dedupe (incident_id, dedupe_key),
    KEY ix_alert_incident_events (incident_id, id),
    FOREIGN KEY (incident_id) REFERENCES alert_incidents(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS notifications (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    incident_id BIGINT NOT NULL,
    event_id BIGINT NOT NULL,
    kind VARCHAR(16) NOT NULL,
    severity VARCHAR(16) NOT NULL,
    title VARCHAR(160) NOT NULL,
    body VARCHAR(500) NOT NULL,
    created_at_utc DATETIME(6) NOT NULL,
    suppressed_reason VARCHAR(48) NULL,
    UNIQUE KEY ux_notification_event (event_id),
    KEY ix_notifications_property (property_id, created_at_utc),
    KEY ix_notifications_incident (incident_id, kind, created_at_utc),
    FOREIGN KEY (incident_id) REFERENCES alert_incidents(id),
    FOREIGN KEY (event_id) REFERENCES alert_incident_events(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS notification_recipients (
    notification_id BIGINT NOT NULL,
    user_id VARCHAR(64) NOT NULL,
    in_app BOOLEAN NOT NULL,
    read_at_utc DATETIME(6) NULL,
    PRIMARY KEY (notification_id, user_id),
    KEY ix_notification_recipient_user (user_id, in_app, read_at_utc, notification_id),
    FOREIGN KEY (notification_id) REFERENCES notifications(id)
) ENGINE=InnoDB;

-- One row per optional channel attempt; in-app presence is the recipient row itself.
CREATE TABLE IF NOT EXISTS notification_deliveries (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    notification_id BIGINT NOT NULL,
    user_id VARCHAR(64) NOT NULL,
    channel VARCHAR(16) NOT NULL,
    status VARCHAR(16) NOT NULL,
    reason VARCHAR(64) NULL,
    attempts INT NOT NULL DEFAULT 0,
    created_at_utc DATETIME(6) NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL,
    UNIQUE KEY ux_notification_delivery (notification_id, user_id, channel),
    KEY ix_notification_delivery_status (status, created_at_utc),
    FOREIGN KEY (notification_id) REFERENCES notifications(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS notification_preferences (
    user_id VARCHAR(64) NOT NULL PRIMARY KEY,
    settings JSON NOT NULL,
    updated_at_utc DATETIME(6) NOT NULL
) ENGINE=InnoDB;
