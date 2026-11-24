CREATE VIEW lesson_duplicates AS
SELECT packageId, MIN(id) AS keptId, GROUP_CONCAT(id) AS ids,
    GROUP_CONCAT(DISTINCT source) AS sources,
    GROUP_CONCAT(DISTINCT NULLIF(issue,'')) AS issues
FROM lessons
GROUP BY packageId, groupId,
    CASE WHEN groupId IS NULL THEN groupNumber END,
    CASE WHEN groupId IS NULL THEN level END,
    day,slot,subgroup,week,TRIM(subject),TRIM(teacher),TRIM(room),status
HAVING COUNT(*) > 1;

CREATE TEMP TABLE duplicate_lessons AS SELECT * FROM lesson_duplicates;
UPDATE lessons
SET source=(SELECT sources FROM duplicate_lessons WHERE keptId=lessons.id),
    issue=COALESCE((SELECT issues FROM duplicate_lessons WHERE keptId=lessons.id),'')
WHERE id IN (SELECT keptId FROM duplicate_lessons);
DELETE FROM lessons
WHERE EXISTS (SELECT 1 FROM duplicate_lessons d
    WHERE d.packageId=lessons.packageId AND d.keptId<>lessons.id
      AND instr(',' || d.ids || ',', ',' || lessons.id || ',') > 0);
DROP TABLE duplicate_lessons;
