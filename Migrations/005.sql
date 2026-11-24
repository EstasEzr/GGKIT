CREATE TABLE import_warnings(
    id INTEGER PRIMARY KEY,
    packageId INTEGER NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
    message TEXT NOT NULL
);
CREATE INDEX import_warnings_package ON import_warnings(packageId);
