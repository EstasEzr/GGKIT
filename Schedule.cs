using System.Globalization;
using System.Security.Cryptography;

static class Schedule
{
    static readonly string[] Starts = ["08:15","09:10","10:05","11:00","11:55","12:50","13:45","14:40","15:35","16:30","17:25","18:20","19:15"];
    static readonly string[] Ends = ["09:00","09:55","10:50","11:45","12:40","13:35","14:30","15:25","16:20","17:15","18:10","19:05","20:00"];
    static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static int Day(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;
    static DateOnly Parse(string? text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year is >= 1900 and <= 2200 ? date : throw new InputError("Укажите корректную дату.");
    static int Week(DateOnly date, Row? calendar)
    {
        if (calendar is null) return 0;
        var anchor = Parse(calendar.S("anchor"));
        var difference = date.DayNumber - Day(date) - anchor.DayNumber + Day(anchor);
        return difference / 7 % 2 == 0 ? (int)calendar.N("week") : 3 - (int)calendar.N("week");
    }
    static Row? Calendar(Db db) => db.One("SELECT anchor,week FROM academic_calendar WHERE id=1");
    static void Review(HttpContext c) => Portal.Allow(Portal.User(c).Has("admin") || Portal.User(c).Has("academic"));
    static void Admin(HttpContext c) => Portal.Allow(Portal.User(c).Has("admin"));
    static Row Draft(Db db, long id) => Portal.Find(db, "SELECT * FROM packages WHERE id=$0 AND published=0", id);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/calendar", (HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            Portal.Allow(u.Has("admin") || u.Has("academic"));
            return Results.Json(Calendar(db));
        });
        api.MapPut("/calendar", (CalendarInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var u = Portal.User(c);
            Review(c);
            var anchor = Iso(Parse(input.Anchor));
            if (input.Week is not (1 or 2)) throw new InputError("Выберите тип недели.");
            db.Run("INSERT INTO academic_calendar VALUES(1,$0,$1) ON CONFLICT(id) DO UPDATE SET anchor=excluded.anchor,week=excluded.week", anchor, input.Week);
            Portal.Audit(db, u, "schedule.calendar", 1);
            return Results.NoContent();
        }));
        api.MapGet("/schedule", (string from, long? group, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            var date = Parse(from);
            var groupId = group ?? u.N("groupId");
            Portal.Allow(groupId == u.N("groupId") || u.Has("admin") || Portal.OwnGroup(db, u, groupId));
            var calendar = Calendar(db);
            var days = new List<object>();
            var basePackage = db.One("SELECT * FROM packages WHERE kind='base' AND published=1");
            var updated = basePackage?.S("updated") ?? "";
            for (var i = 0; i < 7; i++)
            {
                var current = date.AddDays(i);
                var iso = Iso(current);
                var week = Week(current, calendar);
                var rows = new List<Row>();
                if (basePackage is not null)
                    rows = db.All("SELECT *,0 changed FROM lessons WHERE packageId=$0 AND groupId=$1 AND day=$2 AND (week=0 OR week=$3)",
                        basePackage.N("id"), groupId, Day(current), week);
                var changes = db.One("SELECT * FROM packages WHERE kind='changes' AND starts=$0 AND published=1", iso);
                if (changes is not null)
                {
                    if (string.CompareOrdinal(changes.S("updated"), updated) > 0) updated = changes.S("updated");
                    var replacements = db.All("SELECT *,1 changed FROM lessons WHERE packageId=$0 AND groupId=$1", changes.N("id"), groupId);
                    foreach (var replacement in replacements.OrderBy(r => r.N("subgroup")))
                    {
                        var slot = replacement.N("slot");
                        var sub = replacement.N("subgroup");
                        var affected = rows.Where(r => (slot == 0 || r.N("slot") == slot) && (sub == 0 || r.N("subgroup") == sub || r.N("subgroup") == 0)).ToList();
                        foreach (var row in affected)
                        {
                            if (sub != 0 && row.N("subgroup") == 0) row["subgroup"] = 3 - sub;
                            else rows.Remove(row);
                        }
                        rows.Add(replacement);
                    }
                }
                if (u.N("subgroup") != 0) rows.RemoveAll(r => r.N("subgroup") != 0 && r.N("subgroup") != u.N("subgroup"));
                days.Add(new { date = iso, week, lessons = rows.OrderBy(r => r.N("slot")).Select(r => new {
                    id = r.N("id"), slot = r.N("slot"), subgroup = r.N("subgroup"), subject = r.S("subject"),
                    teacher = r.S("teacher"), room = r.S("room"), status = r.S("status"), changed = r.N("changed") != 0
                }).ToArray() });
            }
            return new { days, updated, bells = Starts.Select((start, index) => new { slot = index + 1, start, end = Ends[index] }) };
        });

        api.MapGet("/packages", (HttpContext c, Db db) =>
        {
            Review(c);
            return db.All("SELECT id,kind,starts,ends,week,published,updated FROM packages ORDER BY id DESC");
        });
        api.MapPost("/packages", async (HttpContext c, Db db) =>
        {
            Admin(c);
            if (!c.Request.HasFormContentType) throw new InputError("Выберите файл XLSX или DOCX.");
            var form = await c.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length is 0 or > 8_388_608) throw new InputError("Выберите файл до 8 МБ.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            string kind;
            string starts;
            string ends;
            int week;
            List<Imported> rows;
            List<string> warnings;
            HashSet<(string Number, string Level)> baseGroups = [];
            try
            {
                switch (Path.GetExtension(file.FileName).ToLowerInvariant())
                {
                    case ".xlsx":
                        kind = "base";
                        var minsk = TimeZoneInfo.FindSystemTimeZoneById("Europe/Minsk");
                        starts = ends = Iso(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, minsk).DateTime));
                        week = 0;
                        (rows, baseGroups, warnings) = OfficeImport.Xlsx(bytes);
                        break;
                    case ".docx":
                        kind = "changes";
                        var changes = OfficeImport.Docx(bytes);
                        starts = ends = Iso(changes.Date);
                        week = changes.Week;
                        rows = changes.Rows;
                        warnings = changes.Warnings;
                        break;
                    default: throw new InputError("Выберите файл XLSX или DOCX.");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Xml.XmlException
                or ArgumentException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new InputError("Не удалось разобрать файл. Проверьте формат и структуру шаблона.");
            }
            if (rows.Count == 0)
                throw new InputError(warnings.Count == 0 ? "В файле не найдено записей расписания." :
                    "В файле нет пригодных записей. " + string.Join(" ", warnings.Take(3)));
            var digest = "3:" + Convert.ToHexString(SHA256.HashData(bytes));
            return db.Write(() =>
            {
                var known = kind == "base"
                    ? db.One("SELECT id,published FROM packages WHERE kind='base' AND digest=$0 ORDER BY published DESC,id DESC LIMIT 1", digest)
                    : db.One("SELECT id,published FROM packages WHERE kind='changes' AND starts=$0 AND digest=$1", starts, digest);
                if (known is not null)
                {
                    if (known.N("published") != 0 || kind != "base")
                        return new { id = known.N("id"), published = known.N("published") != 0 };
                    var unresolved = db.All("SELECT DISTINCT groupNumber,level FROM lessons WHERE packageId=$0 AND groupId IS NULL", known.N("id"))
                        .Select(row => (row.S("groupNumber"), row.S("level")));
                    baseGroups.IntersectWith(unresolved);
                }
                var matches = new Dictionary<(string Number, string Level), long?>();
                foreach (var group in baseGroups)
                {
                    var candidates = db.All("SELECT id FROM groups WHERE number=$0 AND level=$1", group.Number, group.Level);
                    matches[group] = candidates.Count switch
                    {
                        0 => db.Insert("INSERT INTO groups(number,level,year) VALUES($0,$1,$2)",
                            group.Number, group.Level, Parse(starts).Year),
                        1 => candidates[0].N("id"),
                        _ => null
                    };
                }
                if (known is not null)
                {
                    if (known.N("published") == 0)
                        foreach (var (group, groupId) in matches.Where(match => match.Value is not null))
                            db.Run("UPDATE lessons SET groupId=$0 WHERE packageId=$1 AND groupNumber=$2 AND level=$3 AND groupId IS NULL",
                                groupId, known.N("id"), group.Number, group.Level);
                    Deduplicate(db, known.N("id"));
                    return new { id = known.N("id"), published = known.N("published") != 0 };
                }
                var id = db.Insert("INSERT INTO packages(kind,starts,ends,anchor,week,digest) VALUES($0,$1,$2,$1,$3,$4)", kind, starts, ends, week, digest);
                foreach (var row in rows)
                {
                    var key = (row.GroupNumber, row.Level);
                    if (!matches.TryGetValue(key, out var groupId))
                    {
                        var candidates = db.All("SELECT id FROM groups WHERE number=$0 AND ($1='' OR level=$1)", row.GroupNumber, row.Level);
                        matches[key] = groupId = candidates.Count == 1 ? candidates[0].N("id") : null;
                    }
                    db.Run("""
                        INSERT INTO lessons(packageId,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue)
                        VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                        """, id, groupId, row.GroupNumber, row.Level,
                        row.Day, row.Slot, row.Subgroup, row.Week, row.Subject, row.Teacher, row.Room, row.Status, row.Source, row.Issue);
                }
                foreach (var warning in warnings)
                    db.Run("INSERT INTO import_warnings(packageId,message) VALUES($0,$1)", id, warning);
                Deduplicate(db, id);
                return new { id, published = false };
            });
        });
        api.MapPost("/packages/manual", (CalendarInput input, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            var date = Iso(Parse(input.Anchor));
            var id = db.Insert("INSERT INTO packages(kind,starts,ends,anchor,week,digest) VALUES('changes',$0,$0,$0,0,$1)", date, Guid.NewGuid().ToString());
            var previous = db.One("SELECT id FROM packages WHERE kind='changes' AND starts=$0 AND published=1", date);
            if (previous is not null) db.Run("""
                INSERT INTO lessons(packageId,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue)
                SELECT $0,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue FROM lessons WHERE packageId=$1
                """, id, previous.N("id"));
            return new { id };
        }));
        api.MapPost("/packages/{id:long}/edit", (long id, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            var package = Portal.Find(db, "SELECT * FROM packages WHERE id=$0", id);
            if (package.N("published") == 0 && package["updated"] is null) return new { id };
            var existing = db.One("SELECT id FROM packages WHERE editSource=$0 AND published=0 AND updated IS NULL", id);
            if (existing is not null) return new { id = existing.N("id") };
            var draft = db.Insert("""
                INSERT INTO packages(kind,starts,ends,anchor,week,digest,editSource)
                VALUES($0,$1,$2,$3,$4,$5,$6)
                """, package.S("kind"), package.S("starts"), package.S("ends"), package.S("anchor"),
                package.N("week"), Guid.NewGuid().ToString(), id);
            db.Run("""
                INSERT INTO lessons(packageId,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue)
                SELECT $0,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue
                FROM lessons WHERE packageId=$1
                """, draft, id);
            db.Run("INSERT INTO import_warnings(packageId,message) SELECT $0,message FROM import_warnings WHERE packageId=$1", draft, id);
            Portal.Audit(db, Portal.User(c), "schedule.edit", draft);
            return new { id = draft };
        }));
        api.MapDelete("/packages/{id:long}", (long id, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            Portal.Find(db, "SELECT id FROM packages WHERE id=$0", id);
            db.Run("DELETE FROM packages WHERE id=$0", id);
            Portal.Audit(db, Portal.User(c), "schedule.delete", id);
            return Results.NoContent();
        }));
        api.MapGet("/packages/{id:long}", (long id, HttpContext c, Db db) =>
        {
            Review(c);
            var package = Portal.Find(db, "SELECT * FROM packages WHERE id=$0", id);
            var calendar = Calendar(db);
            var week = Week(Parse(package.S("starts")), calendar);
            var rows = Rows(db, id);
            var previous = db.One("""
                SELECT id FROM packages WHERE kind=$0 AND published=1 AND id<>$1 AND ($0='base' OR starts=$2)
                """, package.S("kind"), id, package.S("starts"));
            var before = previous is null ? [] : Rows(db, previous.N("id"));
            var warnings = db.All("SELECT message FROM import_warnings WHERE packageId=$0 ORDER BY id", id)
                .Select(row => row.S("message")).ToArray();
            return new { package, rows, week, warnings, differences = Diff(before, rows), conflicts = Conflicts(rows) };
        });
        api.MapPut("/packages/{id:long}/lessons/{lesson:long}/week", (long id, long lesson, CalendarInput input, HttpContext c, Db db) => db.Write(() =>
        {
            Review(c);
            if (Draft(db, id).S("kind") != "base" || input.Week is < 0 or > 2)
                throw new InputError("Укажите тип недели для базового расписания.");
            if (db.Run("UPDATE lessons SET week=$0 WHERE packageId=$1 AND id=$2", input.Week, id, lesson) == 0)
                throw new InputError("Запись не найдена.", 404);
            Deduplicate(db, id);
            Portal.Audit(db, Portal.User(c), "schedule.lesson-week", lesson);
            return Results.NoContent();
        }));
        api.MapPut("/packages/{id:long}/groups", (long id, Mapping input, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            Draft(db, id);
            Portal.Find(db, "SELECT id FROM groups WHERE id=$0 AND ($1='' OR level=$1)", input.GroupId, input.Level);
            db.Run("UPDATE lessons SET groupId=$0 WHERE packageId=$1 AND groupNumber=$2 AND level=$3", input.GroupId, id, input.Number, input.Level);
            Deduplicate(db, id);
            return Results.NoContent();
        }));
        api.MapPut("/packages/{id:long}/lessons/{lesson:long}", (long id, long lesson, LessonInput input, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            var p = Draft(db, id);
            if (lesson != 0) Portal.Find(db, "SELECT id FROM lessons WHERE id=$0 AND packageId=$1", lesson, id);
            var group = Portal.Find(db, "SELECT * FROM groups WHERE id=$0", input.GroupId);
            var day = p.S("kind") == "changes" ? Day(Parse(p.S("starts"))) : input.Day;
            if (day is < 1 or > 7 || input.Slot is < 0 or > 24 || input.Subgroup is < 0 or > 2 || input.Week is < 0 or > 2 ||
                input.Status is not ("lesson" or "practice" or "cancel") || (input.Slot == 0) != (input.Status == "practice"))
                throw new InputError("Проверьте день, номер урока, подгруппу и вид записи.");
            var subject = Portal.Required(input.Subject, "Предмет или примечание");
            var week = p.S("kind") == "base" ? input.Week : 0;
            if (lesson == 0) db.Run("""
                INSERT INTO lessons(packageId,groupId,groupNumber,level,day,slot,subgroup,week,subject,teacher,room,status,source,issue)
                VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,'Вручную','')
                """, id, input.GroupId, group.S("number"), group.S("level"), day, input.Slot, input.Subgroup, week, subject, input.Teacher ?? "", input.Room ?? "", input.Status);
            else db.Run("""
                UPDATE lessons SET groupId=$0,day=$1,slot=$2,subgroup=$3,week=$4,subject=$5,teacher=$6,room=$7,status=$8,issue='' WHERE id=$9
                """, input.GroupId, day, input.Slot, input.Subgroup, week, subject, input.Teacher ?? "", input.Room ?? "", input.Status, lesson);
            Deduplicate(db, id);
            return Results.NoContent();
        }));
        api.MapDelete("/packages/{id:long}/lessons/{lesson:long}", (long id, long lesson, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            Draft(db, id);
            db.Run("DELETE FROM lessons WHERE id=$0 AND packageId=$1", lesson, id);
            return Results.NoContent();
        }));
        api.MapPost("/packages/{id:long}/publish", (long id, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            var p = Portal.Find(db, "SELECT * FROM packages WHERE id=$0", id);
            if (p.N("published") == 1) return Results.NoContent();
            var rows = Rows(db, id);
            if (rows.Any(r => r.N("groupId") == 0 || r.S("issue").Length > 0) || Conflicts(rows).Count != 0)
                throw new InputError("Разрешите ошибки и конфликты в предпросмотре.");

            if (p.S("kind") == "base" && !new[] { "ПТО", "ССО" }.All(level =>
                db.One("SELECT 1 FROM lessons l JOIN groups g ON g.id=l.groupId WHERE l.packageId=$0 AND g.level=$1", id, level) is not null))
                throw new InputError("В базовой версии должны присутствовать группы ПТО и ССО.");
            if (p.S("kind") == "base" && rows.Any(r => r.N("week") != 0))
                db.Run("INSERT INTO academic_calendar VALUES(1,$0,1) ON CONFLICT(id) DO NOTHING", p.S("starts"));
            db.Run("UPDATE packages SET published=0 WHERE kind=$0 AND published=1 AND ($0='base' OR starts=$1)", p.S("kind"), p.S("starts"));
            db.Run("UPDATE packages SET published=1,updated=$0 WHERE id=$1", Portal.Now(), id);
            Portal.Audit(db, Portal.User(c), "schedule.publish", id);
            return Results.NoContent();
        }));
        api.MapPost("/packages/{id:long}/unpublish", (long id, HttpContext c, Db db) => db.Write(() =>
        {
            Admin(c);
            if (db.Run("UPDATE packages SET published=0 WHERE id=$0 AND published=1", id) > 0)
                Portal.Audit(db, Portal.User(c), "schedule.unpublish", id);
            return Results.NoContent();
        }));
    }

    static List<Row> Rows(Db db, long id) => db.All("SELECT * FROM lessons WHERE packageId=$0 ORDER BY id", id);
    static void Deduplicate(Db db, long packageId)
    {
        foreach (var duplicate in db.All("SELECT * FROM lesson_duplicates WHERE packageId=$0", packageId))
        {
            var keptId = duplicate.N("keptId");
            db.Run("UPDATE lessons SET source=$0,issue=$1 WHERE id=$2",
                duplicate.S("sources"), duplicate.S("issues"), keptId);
            foreach (var id in duplicate.S("ids").Split(',').Select(long.Parse))
                if (id != keptId) db.Run("DELETE FROM lessons WHERE id=$0", id);
        }
    }

    static Dictionary<long, List<long>> Conflicts(List<Row> rows)
    {
        var conflicts = new Dictionary<long, List<long>>();
        void Add(long id, long other)
        {
            if (!conflicts.TryGetValue(id, out var peers)) conflicts[id] = peers = [];
            peers.Add(other);
        }
        foreach (var set in rows.Where(r => r.N("groupId") != 0 && (r.N("slot") > 0 || r.S("status") == "practice")).GroupBy(r => (r.N("groupId"), r.N("day"))))
        {
            var list = set.ToArray();
            for (var i = 0; i < list.Length; i++)
            for (var j = i + 1; j < list.Length; j++)
            {
                bool Overlap(string key) => list[i].N(key) == 0 || list[j].N(key) == 0 || list[i].N(key) == list[j].N(key);
                if (Overlap("slot") && Overlap("week") && Overlap("subgroup"))
                {
                    Add(list[i].N("id"), list[j].N("id"));
                    Add(list[j].N("id"), list[i].N("id"));
                }
            }
        }
        return conflicts;
    }
    static object Diff(List<Row> before, List<Row> after)
    {
        static string Key(Row r) => string.Join('|', r.N("groupId"), r.N("day"), r.N("slot"), r.N("subgroup"), r.N("week"));
        static string Value(Row r) => string.Join('|', r.S("subject"), r.S("teacher"), r.S("room"), r.S("status"));
        var old = before.ToLookup(Key, Value);
        var next = after.ToLookup(Key, Value);
        return new {
            added = after.Where(r => !old.Contains(Key(r))),
            changed = after.Where(r => old.Contains(Key(r)) && !old[Key(r)].Contains(Value(r))),
            removed = before.Where(r => !next.Contains(Key(r)))
        };
    }
}

record CalendarInput(string Anchor, int Week);
record Mapping(string Number, string Level, long GroupId);
record LessonInput(long GroupId, int Day, int Slot, int Subgroup, int Week, string Subject, string? Teacher, string? Room, string Status);
