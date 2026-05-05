# PostgreSQL Roles And Grants For ShopInventory

Primary host: `10.10.10.9`
Standby host: `10.10.10.58`

Run these statements on the primary server `10.10.10.9` while connected as the `postgres` superuser from pgAdmin.

## 1. Create Or Update The Roles

Run this first while connected to the `postgres` database:

```sql
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_roles
        WHERE rolname = 'shopinventory'
    ) THEN
        CREATE ROLE shopinventory
            LOGIN
            PASSWORD 'REPLACE_WITH_APP_PASSWORD'
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOREPLICATION;
    ELSE
        ALTER ROLE shopinventory WITH
            LOGIN
            PASSWORD 'REPLACE_WITH_APP_PASSWORD'
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOREPLICATION;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_roles
        WHERE rolname = 'repl_user'
    ) THEN
        CREATE ROLE repl_user
            LOGIN
            REPLICATION
            PASSWORD 'REPLACE_WITH_REPLICATION_PASSWORD'
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE;
    ELSE
        ALTER ROLE repl_user WITH
            LOGIN
            REPLICATION
            PASSWORD 'REPLACE_WITH_REPLICATION_PASSWORD'
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE;
    END IF;
END
$$;
```

Verify:

```sql
SELECT rolname, rolcanlogin, rolreplication
FROM pg_roles
WHERE rolname IN ('shopinventory', 'repl_user')
ORDER BY rolname;
```

## 2. Create The Databases If They Do Not Already Exist

Still connected to the `postgres` database, check first:

```sql
SELECT datname
FROM pg_database
WHERE datname IN ('shopinventory', 'shopinventoryweb')
ORDER BY datname;
```

If `shopinventory` is missing, run:

```sql
CREATE DATABASE shopinventory
    OWNER shopinventory
    ENCODING 'UTF8'
    TEMPLATE template0;
```

If `shopinventoryweb` is missing, run:

```sql
CREATE DATABASE shopinventoryweb
    OWNER shopinventory
    ENCODING 'UTF8'
    TEMPLATE template0;
```

## 3. Apply Ownership And Runtime Grants In The API Database

Reconnect in pgAdmin to the `shopinventory` database, then run:

```sql
ALTER DATABASE shopinventory OWNER TO shopinventory;
GRANT CONNECT, TEMP ON DATABASE shopinventory TO shopinventory;

ALTER SCHEMA public OWNER TO shopinventory;
GRANT USAGE, CREATE ON SCHEMA public TO shopinventory;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO shopinventory;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO shopinventory;
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO shopinventory;

ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON FUNCTIONS TO shopinventory;

ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON FUNCTIONS TO shopinventory;
```

## 4. Apply Ownership And Runtime Grants In The Web Database

Reconnect in pgAdmin to the `shopinventoryweb` database, then run:

```sql
ALTER DATABASE shopinventoryweb OWNER TO shopinventory;
GRANT CONNECT, TEMP ON DATABASE shopinventoryweb TO shopinventory;

ALTER SCHEMA public OWNER TO shopinventory;
GRANT USAGE, CREATE ON SCHEMA public TO shopinventory;

GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO shopinventory;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO shopinventory;
GRANT ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public TO shopinventory;

ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    GRANT ALL PRIVILEGES ON FUNCTIONS TO shopinventory;

ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO shopinventory;
ALTER DEFAULT PRIVILEGES FOR ROLE shopinventory IN SCHEMA public
    GRANT ALL PRIVILEGES ON FUNCTIONS TO shopinventory;
```

## 5. Replication Sanity Checks

After the standby is seeded and started, run these on the primary `10.10.10.9`:

```sql
SELECT application_name, client_addr, state, sync_state
FROM pg_stat_replication;
```

Run this on the standby `10.10.10.58`:

```sql
SELECT pg_is_in_recovery();
```

Expected result on the standby:

```sql
pg_is_in_recovery
-------------------
true
```

## 6. Important Notes

- Replace both password placeholders before running the role block.
- The same `shopinventory` login is used for both `shopinventory` and `shopinventoryweb` in the current templates.
- These grants are designed to work whether the existing objects were created by `postgres` or by `shopinventory`.
- The `pg_hba.conf` templates already use the confirmed host IPs `10.10.10.9` and `10.10.10.58`.