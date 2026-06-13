-- app_settings: runtime-tunable key/value config, changed live via /api/settings (no restart).
-- Defaults are seeded by the API on first start. See docs/CONFIGURATION.md for the keys.
-- No foreign keys. `Key` is a reserved word in MySQL/MariaDB → must be backtick-quoted.
USE AWOXHomeDB;

CREATE TABLE IF NOT EXISTS app_settings (
  `Key`        VARCHAR(64)  NOT NULL,
  Value        VARCHAR(512) NOT NULL,
  Description  VARCHAR(256) NULL,
  UpdatedUtc   DATETIME(6)  NOT NULL,
  PRIMARY KEY (`Key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
