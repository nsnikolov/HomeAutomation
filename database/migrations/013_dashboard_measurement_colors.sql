ALTER TABLE dashboard_values
    ADD COLUMN color_mode VARCHAR(9) NOT NULL DEFAULT 'automatic',
    ADD COLUMN custom_color CHAR(7) CHARACTER SET ascii COLLATE ascii_bin NULL,
    ADD CONSTRAINT ck_dashboard_measurement_color CHECK (
        (color_mode = 'automatic' AND custom_color IS NULL) OR
        (color_mode = 'fixed' AND custom_color IS NOT NULL)
    );
