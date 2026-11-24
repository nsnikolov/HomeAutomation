CREATE TABLE IF NOT EXISTS dashboard_groups (
    property_id BIGINT NOT NULL,
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    position SMALLINT UNSIGNED NOT NULL,
    title VARCHAR(96) NOT NULL,
    hidden BOOLEAN NOT NULL,
    collapsed BOOLEAN NOT NULL,
    PRIMARY KEY (property_id, id),
    UNIQUE KEY ux_dashboard_group_order (property_id, position),
    FOREIGN KEY (property_id) REFERENCES properties(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS dashboard_widgets (
    property_id BIGINT NOT NULL,
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    group_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    position SMALLINT UNSIGNED NOT NULL,
    title VARCHAR(96) NOT NULL,
    display_mode VARCHAR(8) NOT NULL,
    navigation_source VARCHAR(192) CHARACTER SET ascii COLLATE ascii_bin NULL,
    PRIMARY KEY (property_id, id),
    UNIQUE KEY ux_dashboard_widget_order (property_id, group_id, position),
    FOREIGN KEY (property_id, group_id) REFERENCES dashboard_groups(property_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS dashboard_values (
    property_id BIGINT NOT NULL,
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    widget_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    position SMALLINT UNSIGNED NOT NULL,
    source_key VARCHAR(192) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    label VARCHAR(96) NULL,
    PRIMARY KEY (property_id, id),
    UNIQUE KEY ux_dashboard_value_order (property_id, widget_id, position),
    FOREIGN KEY (property_id, widget_id) REFERENCES dashboard_widgets(property_id, id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
