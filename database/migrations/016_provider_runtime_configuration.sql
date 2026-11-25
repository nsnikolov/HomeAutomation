ALTER TABLE configuration_profiles
    ADD COLUMN operational_settings JSON NULL,
    ADD CONSTRAINT ck_configuration_operational_settings
        CHECK (operational_settings IS NULL OR JSON_TYPE(operational_settings) = 'OBJECT');
