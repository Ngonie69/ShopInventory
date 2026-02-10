-- Initialize databases for ShopInventory
-- This script runs automatically when the PostgreSQL container starts

-- Create web cache database if it doesn't exist
SELECT 'CREATE DATABASE shopinventoryweb'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'shopinventoryweb')\gexec

-- Grant permissions
GRANT ALL PRIVILEGES ON DATABASE shopinventory TO shopinventory;
GRANT ALL PRIVILEGES ON DATABASE shopinventoryweb TO shopinventory;
