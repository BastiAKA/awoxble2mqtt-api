-- AwoxController — database bootstrap (MariaDB / MySQL).
--
-- Creates the database and an application user. The TABLES are created by the per-table scripts in
-- database/tables/ (run by CreateDBEnv.ps1) — see the note at the bottom.
--
-- Easiest: run the whole environment in one go (DB + user + all tables, correct order):
--     ./database/CreateDBEnv.ps1 -Password <rootPassword>          # add -MySqlHost <pi> for a remote DB
--
-- Or by hand: this file first, then each database/tables/NN_*.sql in numeric order:
--     mysql -u root -p < database/InitDB.sql
--
-- CHANGE THESE to your own values, then put the SAME database name / user / password into
-- ConnectionStrings:AwoxDb in appsettings.Development.json (see docs/CONFIGURATION.md):
--     database : AWOXHomeDB
--     user     : api_User
--     password : StrongPassword123!   <-- change this!
--     host     : 'localhost' if the API runs on the same box as the DB; '%' for any host (LAN).

CREATE DATABASE IF NOT EXISTS AWOXHomeDB
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_bin;

-- 'localhost' = API and DB on the same machine (the Pi). Use 'api_User'@'%' instead if the API
-- (or the dev box) connects over the network — and then open MariaDB's bind-address accordingly.
CREATE USER IF NOT EXISTS 'api_User'@'localhost'
  IDENTIFIED BY 'StrongPassword123!';

GRANT ALL PRIVILEGES ON AWOXHomeDB.* TO 'api_User'@'localhost';
FLUSH PRIVILEGES;

-- Schema: defined explicitly in database/tables/NN_*.sql (meshes → lamps → app_settings → scenes →
-- scene_items). EF Core MigrateAsync currently fails on MariaDB, so the app runs with
-- Database:Bootstrap = ensureCreated (see docs/CONFIGURATION.md) — which is a no-op once these tables
-- exist. Future schema changes ship as new database/tables/*.sql applied by hand (or re-run CreateDBEnv).
