-- meshes: an AwoX BLE mesh network and its credentials. Referenced by lamps.MeshNetworkId.
-- No foreign keys → created FIRST. Column types mirror the EF Core model (MeshNetwork entity).
USE AWOXHomeDB;

CREATE TABLE IF NOT EXISTS meshes (
  Id            INT          NOT NULL AUTO_INCREMENT,
  MeshName      VARCHAR(64)  NOT NULL,
  MeshPassword  VARCHAR(64)  NOT NULL,
  MeshKey       VARCHAR(128) NOT NULL,
  Service       VARCHAR(32)  NOT NULL,
  PRIMARY KEY (Id),
  UNIQUE KEY IX_meshes_Service (Service)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
