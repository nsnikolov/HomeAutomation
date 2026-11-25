ALTER TABLE dashboard_widgets
    ADD COLUMN width_columns INT NOT NULL DEFAULT 1,
    ADD COLUMN measurement_columns INT NOT NULL DEFAULT 2,
    ADD CONSTRAINT ck_dashboard_widget_width CHECK (width_columns BETWEEN 1 AND 4),
    ADD CONSTRAINT ck_dashboard_measurement_columns CHECK (measurement_columns BETWEEN 1 AND 4);
