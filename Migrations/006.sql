ALTER TABLE materials ADD COLUMN restricted INTEGER NOT NULL DEFAULT 0 CHECK(restricted IN (0,1));
CREATE TABLE material_groups(
    materialId INTEGER NOT NULL REFERENCES materials(id) ON DELETE CASCADE,
    groupId INTEGER NOT NULL REFERENCES groups(id),
    PRIMARY KEY(materialId,groupId)
);
