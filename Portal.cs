using Microsoft.AspNetCore.Identity;

static class Portal
{
    public static readonly string[] StaffRoles = ["curator", "teacher", "deputy", "academic", "admin", "sysadmin"];
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public static Row User(HttpContext context) => (Row)context.Items["user"]!;
    public static string LoginName(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    public static string Required(string? value, string label, int max = 200)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > max) throw new InputError("Проверьте поле «" + label + "».");
        return text;
    }
    public static void Allow(bool condition)
    {
        if (!condition) throw new InputError("Доступ ограничен.", 403);
    }
    public static Row Find(Db db, string sql, params object?[] args) => db.One(sql, args) ?? throw new InputError("Запись не найдена.", 404);
    public static bool Staff(Row user) => StaffRoles.Any(user.Has);
    public static bool OwnGroup(Db db, Row user, long id) =>
        user.Has("curator") && db.One("SELECT 1 FROM curators WHERE userId=$0 AND groupId=$1", user.N("id"), id) is not null;
    public static void Audit(Db db, Row user, string operation, long id) =>
        db.Run("INSERT INTO audit(author,operation,target,created) VALUES($0,$1,$2,$3)", user.N("id"), operation, id, Now());

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/groups", (HttpContext c, Db db) =>
        {
            var u = User(c);
            return db.All("""
                SELECT g.*, COALESCE(c.userId=$0,0) managed, c.userId IS NULL available,
                    CASE WHEN $4 THEN c.userId END curatorId
                FROM groups g LEFT JOIN curators c ON c.groupId=g.id
                WHERE $1 OR g.id=$2 OR ($3 AND (c.userId=$0 OR c.userId IS NULL)) ORDER BY level,number,year
                """, u.N("id"), u.Has("admin") || u.Has("sysadmin") || u.Has("teacher"), u.N("groupId"), u.Has("curator"), u.Has("admin"));
        });
        api.MapPut("/groups/{id:long}/curator", (long id, CuratorInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var user = User(c);
            var admin = user.Has("admin");
            var curatorId = input.CuratorId ?? user.N("id");
            Allow(admin || (user.Has("curator") && curatorId == user.N("id")));
            Find(db, "SELECT id FROM groups WHERE id=$0", id);
            var curator = Find(db, "SELECT roles FROM users WHERE id=$0 AND active=1 AND groupId IS NULL", curatorId);
            if (!curator.Has("curator")) throw new InputError("Выберите действующего куратора.");
            var current = db.One("SELECT userId FROM curators WHERE groupId=$0", id);
            if (current?.N("userId") == curatorId) return Results.NoContent();
            if (!admin && current is not null) throw new InputError("Группа уже закреплена за другим куратором.", 409);
            db.Run("INSERT INTO curators(userId,groupId) VALUES($0,$1) ON CONFLICT(groupId) DO UPDATE SET userId=excluded.userId",
                curatorId, id);
            Audit(db, user, "group.curator", id);
            return Results.NoContent();
        }));
        api.MapPut("/groups/{id:long}", (long id, GroupInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var u = User(c);
            Allow(u.Has("curator") && (id == 0 || OwnGroup(db, u, id)));
            var number = Required(input.Number, "Номер группы").ToUpperInvariant();
            if (input.Level is not ("ПТО" or "ССО") || input.Year is < 1900 or > 2200) throw new InputError("Укажите уровень и год набора.");
            if (!input.Confirm && db.One("SELECT id FROM groups WHERE number=$0 AND id<>$1", number, id) is not null)
                throw new InputError("Группа с таким номером уже существует. Подтвердите сохранение.", 409);
            if (id == 0)
            {
                id = db.Insert("INSERT INTO groups(number,level,year) VALUES($0,$1,$2)", number, input.Level, input.Year);
                db.Run("INSERT INTO curators VALUES($0,$1)", u.N("id"), id);
            }
            else db.Run("UPDATE groups SET number=$0,level=$1,year=$2 WHERE id=$3", number, input.Level, input.Year, id);
            return new { id };
        }));
        api.MapGet("/groups/{id:long}/students", (long id, HttpContext c, Db db) =>
        {
            Allow(OwnGroup(db, User(c), id));
            return db.All("SELECT id,name,book,subgroup,active FROM users WHERE groupId=$0 AND active=1 ORDER BY name", id);
        });
        api.MapPut("/groups/{group:long}/students/{id:long}", (long group, long id, StudentInput input, HttpContext c, Db db, PasswordHasher<string> hash) => db.Write(() =>
        {
            Allow(OwnGroup(db, User(c), group));
            var name = Required(input.Name, "ФИО");
            var book = Required(input.Book, "Номер зачётной книжки");
            var login = LoginName(name);
            var password = id == 0 ? book : input.Password;
            if (password is not null) Required(password, "Пароль", 256);
            var existing = id == 0 ? null : Find(db, "SELECT * FROM users WHERE id=$0 AND groupId=$1 AND active=1", id, group);
            var peers = db.All("SELECT * FROM users WHERE login=$0 AND id<>$1 AND active=1", login, id);
            if (peers.Any(peer => password is not null &&
                hash.VerifyHashedPassword("", peer.S("password"), password) != PasswordVerificationResult.Failed))
                throw new InputError("Такой пароль уже используется учащимся с тем же ФИО.");
            if (existing is not null && login != existing.S("login") && peers.Count != 0 && password is null)
                throw new InputError("При совпадении ФИО укажите новый пароль.");
            var stored = password is null ? existing!.S("password") : hash.HashPassword("", password);
            if (id == 0) id = db.Insert("INSERT INTO users(name,login,password,roles,groupId,book,subgroup) VALUES($0,$1,$2,'student',$3,$4,$5)",
                name, login, stored, group, book, input.Subgroup);
            else db.Run("UPDATE users SET name=$0,login=$1,password=$2,book=$3,subgroup=$4,stamp=$5 WHERE id=$6",
                name, login, stored, book, input.Subgroup, Guid.NewGuid().ToString(), id);
            return new { id };
        }));
        api.MapDelete("/groups/{group:long}/students/{id:long}", (long group, long id, HttpContext c, Db db) =>
        {
            Allow(OwnGroup(db, User(c), group));
            db.Run("UPDATE users SET active=0,stamp=$0 WHERE id=$1 AND groupId=$2", Guid.NewGuid().ToString(), id, group);
            return Results.NoContent();
        });
        api.MapGet("/staff", (HttpContext c, Db db) =>
        {
            var u = User(c);
            Allow(Staff(u));
            var staff = db.All("SELECT id,name,login,roles,active FROM users WHERE groupId IS NULL AND ($0 OR active=1) ORDER BY name", u.Has("sysadmin") || u.Has("admin"));

            return staff;
        });
        api.MapPut("/staff/{id:long}", (long id, StaffInput input, HttpContext c, Db db, PasswordHasher<string> hash) => db.Write(() =>
        {
            var u = User(c);
            var systemAdmin = u.Has("sysadmin");
            Allow(systemAdmin || u.Has("admin"));
            var existing = id == 0 ? null : Find(db, "SELECT * FROM users WHERE id=$0 AND groupId IS NULL", id);
            Allow(systemAdmin || existing?.Has("sysadmin") != true);
            var name = Required(input.Name, "ФИО");
            var login = LoginName(Required(input.Login, "Логин"));
            if (input.Roles is null || input.Roles.Length == 0 || input.Roles.Except(StaffRoles).Any() || (input.Roles.Contains("academic") && !input.Roles.Contains("deputy")))
                throw new InputError("Выберите роли сотрудника.");
            Allow(systemAdmin || !input.Roles.Contains("sysadmin"));
            var managingRole = systemAdmin ? "sysadmin" : "admin";
            if (u.N("id") == id && (!input.Roles.Contains(managingRole) || !input.Active))
                throw new InputError("Сохраните свою роль администратора и активную учётную запись.");
            if (db.One("SELECT id FROM users WHERE login=$0 AND id<>$1", login, id) is not null)
                throw new InputError("Логин занят.");
            var password = id == 0 || !string.IsNullOrWhiteSpace(input.Password)
                ? hash.HashPassword("", Required(input.Password, "Пароль", 256)) : existing!.S("password");
            var roles = string.Join(',', input.Roles.Distinct());
            if (id == 0) id = db.Insert("INSERT INTO users(name,login,password,roles,active) VALUES($0,$1,$2,$3,$4)", name, login, password, roles, input.Active);
            else db.Run("UPDATE users SET name=$0,login=$1,password=$2,roles=$3,active=$4,stamp=$5 WHERE id=$6",
                name, login, password, roles, input.Active, Guid.NewGuid().ToString(), id);
            if (!input.Active || !input.Roles.Contains("curator"))
                db.Run("DELETE FROM curators WHERE userId=$0", id);
            Audit(db, u, "staff.roles", id);
            return new { id };
        }));
    }
}

record GroupInput(string Number, string Level, int Year, bool Confirm);
record StudentInput(string Name, string Book, int? Subgroup, string? Password);
record StaffInput(string Name, string Login, string? Password, string[] Roles, bool Active);
record CuratorInput(long? CuratorId);
