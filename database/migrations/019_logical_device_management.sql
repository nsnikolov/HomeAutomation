-- Milestone 7E: how each logical identity came to exist, and the lifecycle actions added for its management.
ALTER TABLE logical_devices
    ADD COLUMN creation VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'manual',
    ADD CONSTRAINT ck_logical_device_creation CHECK (creation IN ('manual', 'initialized', 'automatic'));

UPDATE logical_devices l
SET l.creation = 'initialized'
WHERE EXISTS (SELECT 1 FROM logical_device_audit a
              WHERE a.property_id = l.property_id AND a.logical_device_id = l.id AND a.action = 'initialized');

ALTER TABLE logical_device_audit DROP CHECK ck_logical_audit_action;
ALTER TABLE logical_device_audit ADD CONSTRAINT ck_logical_audit_action CHECK (action IN
    ('created', 'initialized', 'updated', 'assigned', 'replaced', 'corrected', 'archived',
     'provisioned', 'unassigned', 'restored', 'resolved'));
