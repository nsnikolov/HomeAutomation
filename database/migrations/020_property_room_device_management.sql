-- Property, room and device management. Additive only: no existing row is changed.
ALTER TABLE properties
    ADD COLUMN description VARCHAR(256) NULL,
    ADD COLUMN creation_request_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ADD COLUMN deleted_at_utc DATETIME(6) NULL,
    ADD UNIQUE KEY ux_property_creation_request (creation_request_id);

ALTER TABLE rooms ADD COLUMN description VARCHAR(256) NULL;

-- display_name: a user-defined label that provider synchronization never writes.
-- retired_at_utc: set when the device moved to another property; the row, its series and history stay with this property.
ALTER TABLE devices
    ADD COLUMN display_name VARCHAR(128) NULL,
    ADD COLUMN retired_at_utc DATETIME(6) NULL;

CREATE TABLE IF NOT EXISTS management_audit (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    property_id BIGINT NOT NULL,
    occurred_at_utc DATETIME(6) NOT NULL,
    actor VARCHAR(64) NOT NULL,
    action VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    device_id BIGINT NULL,
    room_id BIGINT NULL,
    other_property_id BIGINT NULL,
    other_device_id BIGINT NULL,
    detail VARCHAR(256) NULL,
    KEY ix_management_audit_property (property_id, occurred_at_utc, id),
    KEY ix_management_audit_device (device_id, occurred_at_utc, id),
    CONSTRAINT fk_management_audit_property FOREIGN KEY (property_id) REFERENCES properties(id),
    CONSTRAINT ck_management_audit_action CHECK (action IN (
        'propertyCreated', 'propertyUpdated', 'propertyDeleted', 'roomCreated', 'roomUpdated', 'roomDeleted',
        'deviceAssigned', 'deviceRoomChanged', 'deviceRenamed', 'deviceTransferredOut', 'deviceTransferredIn',
        'roomAssignmentsCleared'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
