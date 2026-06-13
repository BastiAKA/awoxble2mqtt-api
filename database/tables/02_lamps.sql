-- lamps: the device registry (one row per bulb). FK MeshNetworkId → meshes(Id), SET NULL when the mesh
-- is deleted. Create AFTER meshes. Column types mirror the EF Core model (LampDevice entity).
--   MeshId     = the bulb's mesh id (0 = broadcast to the whole mesh)
--   Protocol   = 0 tlmesh (Connect-C), 1 zigbee (Connect-Z)
--   LastState  = last-commanded state as JSON ({on,brightness,colorBrightness,color,colorTemp})
USE AWOXHomeDB;

CREATE TABLE IF NOT EXISTS lamps (
  Id                  INT          NOT NULL AUTO_INCREMENT,
  Name                VARCHAR(128) NOT NULL,
  Mac                 VARCHAR(17)  NOT NULL,
  MeshId              INT          NOT NULL,
  MeshNetworkId       INT          NULL,
  Model               VARCHAR(64)  NOT NULL,
  Protocol            INT          NOT NULL,
  DeviceType          VARCHAR(64)  NULL,
  Room                VARCHAR(64)  NULL,
  Enabled             TINYINT(1)   NOT NULL,
  SeparateWhiteColor  TINYINT(1)   NOT NULL,
  LastState           VARCHAR(512) NULL,
  CreatedUtc          DATETIME(6)  NOT NULL,
  PRIMARY KEY (Id),
  UNIQUE KEY IX_lamps_Mac (Mac),
  UNIQUE KEY IX_lamps_Name (Name),
  KEY IX_lamps_MeshNetworkId (MeshNetworkId),
  CONSTRAINT FK_lamps_meshes FOREIGN KEY (MeshNetworkId) REFERENCES meshes (Id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
