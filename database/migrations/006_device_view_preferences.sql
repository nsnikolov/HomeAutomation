CREATE TABLE IF NOT EXISTS device_view_preferences (
    property_id BIGINT NOT NULL,
    user_id CHAR(36) NOT NULL,
    page VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    card_order JSON NOT NULL,
    hidden_devices JSON NOT NULL,
    PRIMARY KEY (property_id, user_id, page),
    FOREIGN KEY (property_id, user_id) REFERENCES property_memberships(property_id, user_id) ON DELETE CASCADE
) ENGINE=InnoDB;
