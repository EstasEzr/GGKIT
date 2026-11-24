ALTER TABLE materials ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
UPDATE materials SET position=-id;
CREATE INDEX material_order ON materials(subjectId,position,id);
