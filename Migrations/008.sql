ALTER TABLE packages ADD COLUMN editSource INTEGER REFERENCES packages(id) ON DELETE SET NULL;
CREATE UNIQUE INDEX package_edit_draft ON packages(editSource) WHERE editSource IS NOT NULL AND published=0 AND updated IS NULL;
