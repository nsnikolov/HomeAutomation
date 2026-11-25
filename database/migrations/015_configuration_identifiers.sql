ALTER TABLE configuration_profiles
    ADD COLUMN identifiers JSON NOT NULL DEFAULT (JSON_OBJECT()),
    ADD CONSTRAINT ck_configuration_identifiers_object CHECK (JSON_TYPE(identifiers) = 'OBJECT');
