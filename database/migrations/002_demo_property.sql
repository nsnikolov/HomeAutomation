INSERT INTO properties (site_key, name, time_zone)
VALUES ('countryside', 'Countryside House', 'Europe/Sofia')
ON DUPLICATE KEY UPDATE id = properties.id;

INSERT INTO rooms (property_id, room_key, name)
SELECT id, 'plant', 'Plant room' FROM properties WHERE site_key = 'countryside'
UNION ALL
SELECT id, 'living', 'Living room' FROM properties WHERE site_key = 'countryside'
UNION ALL
SELECT id, 'utility', 'Utility room' FROM properties WHERE site_key = 'countryside'
ON DUPLICATE KEY UPDATE id = rooms.id;

INSERT INTO installations (property_id, installation_key, name)
SELECT id, 'solar', 'Parallel solar installation' FROM properties WHERE site_key = 'countryside'
ON DUPLICATE KEY UPDATE id = installations.id;

INSERT INTO integration_connections (property_id, provider_code)
SELECT id, 'simulator' FROM properties WHERE site_key = 'countryside'
ON DUPLICATE KEY UPDATE id = integration_connections.id;
