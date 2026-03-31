#!/bin/bash
set -e

echo "=== Setting up replication on primary ==="

sleep 3

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    DO \$\$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'replicator') THEN
            CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD '$REPLICATOR_PASSWORD';
            RAISE NOTICE 'Replicator role created.';
        END IF;
    END\$\$;

    SELECT pg_create_physical_replication_slot('booking_replica_slot')
    WHERE NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = 'booking_replica_slot');

    SELECT pg_create_physical_replication_slot('booking_replica_slot_2')
    WHERE NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = 'booking_replica_slot_2');
EOSQL

# if ! grep -q "host    replication     replicator      all" "$PGDATA/pg_hba.conf"; then
#     echo "host    replication     replicator      all                         scram-sha-256" >> "$PGDATA/pg_hba.conf"
#     echo "Added replication entry to pg_hba.conf"
# fi

# Add pg_hba rule (one rule is enough for all replicas)
if ! grep -q "replicator" "$PGDATA/pg_hba.conf"; then
    echo "host    replication     replicator      0.0.0.0/0          scram-sha-256" >> "$PGDATA/pg_hba.conf"
fi
echo "=== Replication setup completed on primary ==="
