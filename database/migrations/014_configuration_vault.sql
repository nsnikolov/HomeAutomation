CREATE TABLE IF NOT EXISTS configuration_profiles (
    scope_id BIGINT NOT NULL,
    property_id BIGINT NULL,
    provider VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    revision BIGINT NOT NULL,
    poll_seconds INT NOT NULL,
    credentials JSON NOT NULL,
    PRIMARY KEY (scope_id, provider),
    FOREIGN KEY (property_id) REFERENCES properties(id),
    CHECK ((scope_id = 0 AND property_id IS NULL) OR
           (scope_id > 0 AND property_id IS NOT NULL AND scope_id = property_id)),
    CHECK (revision > 0 AND poll_seconds BETWEEN 300 AND 86400 AND JSON_TYPE(credentials) = 'OBJECT')
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS configuration_audit (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    occurred_at_utc DATETIME(6) NOT NULL,
    actor VARCHAR(64) NOT NULL,
    property_id BIGINT NULL,
    provider VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    setting VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    action VARCHAR(20) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CHECK (action IN ('created', 'updated', 'removed', 'credentialRotated')),
    KEY ix_configuration_audit_scope (property_id, occurred_at_utc, id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS configuration_nonces (
    key_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    nonce BINARY(12) NOT NULL,
    PRIMARY KEY (key_id, nonce)
) ENGINE=InnoDB;
