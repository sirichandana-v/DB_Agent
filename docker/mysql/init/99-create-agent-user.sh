#!/bin/bash
# Creates read-only MCP user. Runs only on first MySQL data volume init.
# Passwords come from container env (set via docker compose + .env); never store secrets in this file.

set -euo pipefail

mysql -uroot -p"${MYSQL_ROOT_PASSWORD}" <<-EOSQL
CREATE USER IF NOT EXISTS 'agent_user'@'%' IDENTIFIED BY '${AGENT_USER_PASSWORD}';
GRANT SELECT ON \`db_agent_test\`.* TO 'agent_user'@'%';
FLUSH PRIVILEGES;
EOSQL