-- scenes: a named set of lamps + desired states (e.g. "Filmabend"). No foreign keys.
-- Referenced by scene_items.SceneId, so create BEFORE scene_items.
USE AWOXHomeDB;

CREATE TABLE IF NOT EXISTS scenes (
  Id          INT          NOT NULL AUTO_INCREMENT,
  Name        VARCHAR(128) NOT NULL,
  CreatedUtc  DATETIME(6)  NOT NULL,
  PRIMARY KEY (Id),
  UNIQUE KEY IX_scenes_Name (Name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
