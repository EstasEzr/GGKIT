-- Legacy groups with competing assignments return to the unassigned list.
DELETE FROM curators WHERE groupId IN (SELECT groupId FROM curators GROUP BY groupId HAVING COUNT(*) > 1);
DELETE FROM curators WHERE groupId IS NULL OR NOT EXISTS (
    SELECT 1 FROM users WHERE id=curators.userId AND active=1 AND instr(',' || roles || ',', ',curator,') > 0
);
CREATE UNIQUE INDEX one_curator_per_group ON curators(groupId);
