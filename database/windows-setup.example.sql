-- Manual provisioning example for the database owner, with explicit approval.
-- Verify the schema and account names are unused. Run each statement separately and stop on error.
-- Use the actual account host. Save the generated passwords privately.

CREATE DATABASE homeautomation CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER 'home_migrator'@'127.0.0.1' IDENTIFIED BY RANDOM PASSWORD;
GRANT CREATE, ALTER, REFERENCES, SELECT, INSERT, UPDATE ON homeautomation.* TO 'home_migrator'@'127.0.0.1';

CREATE USER 'home_runtime'@'127.0.0.1' IDENTIFIED BY RANDOM PASSWORD;
GRANT SELECT, INSERT, UPDATE ON homeautomation.* TO 'home_runtime'@'127.0.0.1';

-- After migration 011 creates dashboard_groups, apply this table-specific grant as the database administrator.
-- GRANT DELETE ON homeautomation.dashboard_groups TO 'home_runtime'@'127.0.0.1';

-- For existing installations whose migrator lacks ALTER, before applying 012 and 013:
-- GRANT ALTER ON homeautomation.dashboard_widgets TO 'home_migrator'@'127.0.0.1';
-- GRANT ALTER ON homeautomation.dashboard_values TO 'home_migrator'@'127.0.0.1';
