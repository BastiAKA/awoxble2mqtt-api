-- scene_items: one lamp's entry in a scene + its desired state (JSON, same shape as lamps.LastState).
-- FKs: SceneId → scenes(Id) and LampDeviceId → lamps(Id), both CASCADE on delete. Create LAST (after
-- scenes AND lamps exist).
USE AWOXHomeDB;

CREATE TABLE IF NOT EXISTS scene_items (
  Id            INT          NOT NULL AUTO_INCREMENT,
  SceneId       INT          NOT NULL,
  LampDeviceId  INT          NOT NULL,
  DesiredState  VARCHAR(512) NOT NULL,
  PRIMARY KEY (Id),
  KEY IX_scene_items_SceneId (SceneId),
  KEY IX_scene_items_LampDeviceId (LampDeviceId),
  CONSTRAINT FK_scene_items_scenes FOREIGN KEY (SceneId)      REFERENCES scenes (Id) ON DELETE CASCADE,
  CONSTRAINT FK_scene_items_lamps  FOREIGN KEY (LampDeviceId) REFERENCES lamps  (Id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
