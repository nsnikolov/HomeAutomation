#!/bin/sh
(
  set -eu
  case "$HOME_RUNTIME_PASSWORD" in
    *[!a-zA-Z0-9_-]*|'') echo 'Use only letters, digits, underscores and hyphens for MYSQL_RUNTIME_PASSWORD.' >&2; exit 1 ;;
  esac
  if [ "${#HOME_RUNTIME_PASSWORD}" -lt 16 ]; then
    echo 'MYSQL_RUNTIME_PASSWORD must be at least 16 characters.' >&2
    exit 1
  fi
  MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --protocol=socket --user=root <<SQL
CREATE USER IF NOT EXISTS 'home_runtime'@'%' IDENTIFIED BY '$HOME_RUNTIME_PASSWORD';
GRANT SELECT, INSERT, UPDATE, DELETE ON homeautomation.* TO 'home_runtime'@'%';
SQL
)
