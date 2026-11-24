static class Content
{
    static string? Link(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new InputError("Ссылка должна начинаться с https:// или http://.");
        return url.AbsoluteUri;
    }
    static async Task<(string? Name, byte[]? Data)> Attachment(IFormCollection form)
    {
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, null);
        if (file.Length > 8_388_608) throw new InputError("Максимальный размер файла — 8 МБ.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        return (Path.GetFileName(file.FileName.Replace('\\', '/')), buffer.ToArray());
    }
    static IResult Download(Row row) => row["content"] is byte[] data
        ? Results.File(data, "application/octet-stream", row.S("filename"))
        : throw new InputError("Файл не найден.", 404);
    static string? NewsMediaType(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        _ => null
    };

    static Row WithNewsMedia(Row post)
    {
        post["mediaType"] = NewsMediaType(post.S("filename"));
        return post;
    }

    static Row Subject(Db db, long id) => Portal.Find(db, "SELECT * FROM subjects WHERE id=$0", id);
    static bool Owns(Row u, Row subject) => u.Has("teacher") && subject.N("owner") == u.N("id");
    static bool SubjectVisible(Db db, Row user, Row subject) => Owns(user, subject) ||
        db.One("SELECT 1 FROM subject_groups WHERE subjectId=$0 AND groupId=$1", subject.N("id"), user.N("groupId")) is not null;
    static long[] GroupIds(IFormCollection form)
    {
        var ids = new List<long>();
        foreach (var value in form["groups"])
        {
            if (!long.TryParse(value, out var id) || id <= 0) throw new InputError("Выберите группы.");
            ids.Add(id);
        }
        return ids.Distinct().ToArray();
    }
    static long[] MaterialGroups(Db db, long subjectId, bool restricted, long[] groups)
    {
        if (!restricted) return [];
        if (groups.Length == 0) throw new InputError("Выберите хотя бы одну группу.");
        var allowed = db.All("SELECT groupId FROM subject_groups WHERE subjectId=$0", subjectId)
            .Select(row => row.N("groupId")).ToHashSet();
        if (groups.Any(group => !allowed.Contains(group))) throw new InputError("Выберите группы с доступом к предмету.");
        return groups.Distinct().ToArray();
    }

    static void SaveMaterialGroups(Db db, long materialId, long[] groups)
    {
        db.Run("DELETE FROM material_groups WHERE materialId=$0", materialId);
        foreach (var group in groups)
            db.Run("INSERT INTO material_groups(materialId,groupId) VALUES($0,$1)", materialId, group);
    }

    static bool PostVisible(Db db, Row user, Row post)
    {
        if (post.N("author") == user.N("id") || user.Has("admin")) return true;
        if (post["published"] is null) return false;
        return post.S("audience") switch
        {
            "all" => true,
            "groups" => db.One("SELECT 1 FROM post_groups WHERE postId=$0 AND groupId=$1", post.N("id"), user.N("groupId")) is not null,
            var role when role.StartsWith("roles:") => role[6..].Split(',').Any(user.Has),
            _ => false
        };
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/subjects", (HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            return db.All("""
                SELECT s.*,u.name teacher FROM subjects s JOIN users u ON u.id=s.owner
                WHERE ($0 AND s.owner=$1) OR EXISTS(SELECT 1 FROM subject_groups WHERE subjectId=s.id AND groupId=$2)
                ORDER BY s.title
                """, u.Has("teacher"), u.N("id"), u.N("groupId"));
        });
        api.MapGet("/subjects/{id:long}", (long id, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            var subject = Subject(db, id);
            Portal.Allow(SubjectVisible(db, u, subject));
            var owner = Owns(u, subject);
            var materials = db.All("""
                SELECT id,title,url,filename,restricted FROM materials m WHERE subjectId=$0
                AND ($1 OR restricted=0 OR EXISTS(SELECT 1 FROM material_groups WHERE materialId=m.id AND groupId=$2))
                ORDER BY position,id
                """, id, owner, u.N("groupId"));
            if (owner)
                foreach (var material in materials)
                    material["groups"] = db.All("SELECT groupId FROM material_groups WHERE materialId=$0", material.N("id"))
                        .Select(group => group.N("groupId")).ToArray();
            return new
            {
                subject,
                teacher = Portal.Find(db, "SELECT name FROM users WHERE id=$0", subject.N("owner")).S("name"),
                groups = owner ? db.All("SELECT groupId FROM subject_groups WHERE subjectId=$0", id).Select(g => g.N("groupId")).ToArray() : [],
                materials
            };
        });
        api.MapPut("/subjects/{id:long}", (long id, SubjectInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var u = Portal.User(c);
            Portal.Allow(u.Has("teacher") && (id == 0 || Owns(u, Subject(db, id))));
            var title = Portal.Required(input.Title, "Название предмета");
            if (id == 0) id = db.Insert("INSERT INTO subjects(owner,title) VALUES($0,$1)", u.N("id"), title);
            else db.Run("UPDATE subjects SET title=$0 WHERE id=$1", title, id);
            db.Run("DELETE FROM subject_groups WHERE subjectId=$0", id);
            foreach (var group in (input.Groups ?? []).Distinct()) db.Run("INSERT INTO subject_groups VALUES($0,$1)", id, group);
            return new { id };
        }));
        api.MapPost("/subjects/{id:long}/materials", async (long id, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            Portal.Allow(Owns(u, Subject(db, id)));
            var form = await c.Request.ReadFormAsync();
            var title = Portal.Required(form["title"], "Название материала");
            var url = Link(form["url"]);
            var file = await Attachment(form);
            if (url is null && file.Data is null) throw new InputError("Добавьте файл или ссылку.");
            var restricted = form["restricted"] == "true";
            return db.Write(() =>
            {
                var groups = MaterialGroups(db, id, restricted, GroupIds(form));
                var material = db.Insert("""
                    INSERT INTO materials(subjectId,title,url,filename,content,restricted,position)
                    VALUES($0,$1,$2,$3,$4,$5,(SELECT COALESCE(MIN(position),0)-1 FROM materials WHERE subjectId=$0))
                    """,
                    id, title, url, file.Name, file.Data, restricted);
                SaveMaterialGroups(db, material, groups);
                return Results.NoContent();
            });
        });
        api.MapPut("/subjects/{id:long}/materials/{material:long}/order", (long id, long material, MaterialMove input, HttpContext c, Db db) => db.Write(() =>
        {
            Portal.Allow(Owns(Portal.User(c), Subject(db, id)));
            if (input.Direction is not (-1 or 1)) throw new InputError("Укажите направление перемещения.");
            var rows = db.All("SELECT id,position FROM materials WHERE subjectId=$0 ORDER BY position,id", id);
            var index = rows.FindIndex(row => row.N("id") == material);
            if (index < 0) throw new InputError("Материал не найден.", 404);
            var neighborIndex = index + input.Direction;
            if (neighborIndex >= 0 && neighborIndex < rows.Count)
            {
                var neighbor = rows[neighborIndex];
                db.Run("UPDATE materials SET position=CASE WHEN id=$0 THEN $1 ELSE $2 END WHERE id IN ($0,$3)",
                    material, neighbor.N("position"), rows[index].N("position"), neighbor.N("id"));
            }
            return Results.NoContent();
        }));
        api.MapPut("/subjects/{id:long}/materials/{material:long}/access", (long id, long material, MaterialAccess input, HttpContext c, Db db) => db.Write(() =>
        {
            Portal.Allow(Owns(Portal.User(c), Subject(db, id)));
            Portal.Find(db, "SELECT id FROM materials WHERE id=$0 AND subjectId=$1", material, id);
            var groups = MaterialGroups(db, id, input.Restricted, input.Groups ?? []);
            db.Run("UPDATE materials SET restricted=$0 WHERE id=$1", input.Restricted, material);
            SaveMaterialGroups(db, material, groups);
            return Results.NoContent();
        }));
        api.MapDelete("/subjects/{id:long}/materials/{material:long}", (long id, long material, HttpContext c, Db db) =>
        {
            Portal.Allow(Owns(Portal.User(c), Subject(db, id)));
            db.Run("DELETE FROM materials WHERE subjectId=$0 AND id=$1", id, material);
            return Results.NoContent();
        });
        api.MapGet("/materials/{id:long}/file", (long id, HttpContext c, Db db) =>
        {
            var m = Portal.Find(db, "SELECT * FROM materials WHERE id=$0", id);
            var user = Portal.User(c);
            var subject = Subject(db, m.N("subjectId"));
            Portal.Allow(SubjectVisible(db, user, subject) &&
                (Owns(user, subject) || m.N("restricted") == 0 ||
                 db.One("SELECT 1 FROM material_groups WHERE materialId=$0 AND groupId=$1", id, user.N("groupId")) is not null));
            return Download(m);
        });

        api.MapGet("/posts", (string? topic, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            return db.All("""
                SELECT p.id,p.author,p.title,p.body,p.topic,p.audience,p.published,p.filename,p.url,u.name authorName
                FROM posts p JOIN users u ON u.id=p.author
                WHERE (p.published IS NOT NULL OR p.author=$0) AND ($1 IS NULL OR p.topic=$1)
                ORDER BY p.published DESC,p.id DESC
                """, u.N("id"), topic).Where(p => PostVisible(db, u, p)).Select(WithNewsMedia);
        });
        api.MapGet("/posts/{id:long}", (long id, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            var p = Portal.Find(db, """
                SELECT p.id,p.author,p.title,p.body,p.topic,p.audience,p.published,p.filename,p.url,u.name authorName
                FROM posts p JOIN users u ON u.id=p.author WHERE p.id=$0
                """, id);
            Portal.Allow(PostVisible(db, u, p));
            if (p.N("author") == u.N("id")) p["groups"] = db.All("SELECT groupId FROM post_groups WHERE postId=$0", id).Select(g => g.N("groupId"));
            return WithNewsMedia(p);
        });
        api.MapGet("/posts/{id:long}/media", (long id, HttpContext c, Db db) =>
        {
            var post = Portal.Find(db, "SELECT * FROM posts WHERE id=$0", id);
            Portal.Allow(PostVisible(db, Portal.User(c), post));
            var mediaType = NewsMediaType(post.S("filename"));
            if (mediaType is null || post["content"] is not byte[] bytes)
                throw new InputError("Медиафайл не найден.", 404);
            return Results.File(bytes, mediaType, enableRangeProcessing: mediaType.StartsWith("video/", StringComparison.Ordinal));
        });
        api.MapGet("/posts/{id:long}/file", (long id, HttpContext c, Db db) =>
        {
            var p = Portal.Find(db, "SELECT * FROM posts WHERE id=$0", id);
            Portal.Allow(PostVisible(db, Portal.User(c), p));
            return Download(p);
        });
        api.MapPut("/posts/{id:long}", async (long id, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            Portal.Allow(u.Has("admin") || u.Has("curator"));
            var form = await c.Request.ReadFormAsync();
            var title = Portal.Required(form["title"], "Заголовок");
            var body = Portal.Required(form["body"], "Текст", 30_000);
            var topic = Portal.Required(form["topic"], "Тема");
            var audience = Portal.Required(form["audience"], "Аудитория");
            var groups = GroupIds(form);
            if (audience == "groups")
            {
                if (groups.Length == 0) throw new InputError("Выберите хотя бы одну группу.");
                Portal.Allow(u.Has("admin") || groups.All(g => Portal.OwnGroup(db, u, g)));
            }
            else
            {
                Portal.Allow(u.Has("admin"));
                if (audience != "all" && (!audience.StartsWith("roles:") || audience[6..].Split(',').Except(Portal.StaffRoles.Append("student")).Any()))
                    throw new InputError("Укажите аудиторию.");
            }
            var url = Link(form["url"]);
            var file = await Attachment(form);
            var publish = form["publish"] == "true";
            return db.Write(() =>
            {
                var p = id == 0 ? null : Portal.Find(db, "SELECT * FROM posts WHERE id=$0", id);
                Portal.Allow(p is null || p.N("author") == u.N("id"));
                var published = publish ? p?["published"] ?? Portal.Now() : null;
                var remove = form["removeFile"] == "true";
                var name = remove ? null : file.Name ?? p?["filename"];
                var data = remove ? null : file.Data ?? p?["content"];
                if (id == 0) id = db.Insert("""
                    INSERT INTO posts(author,title,body,topic,audience,published,url,filename,content) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8)
                    """, u.N("id"), title, body, topic, audience, published, url, name, data);
                else db.Run("""
                    UPDATE posts SET title=$0,body=$1,topic=$2,audience=$3,published=$4,url=$5,filename=$6,content=$7 WHERE id=$8
                    """, title, body, topic, audience, published, url, name, data, id);
                db.Run("DELETE FROM post_groups WHERE postId=$0", id);
                if (audience == "groups")
                    foreach (var group in groups) db.Run("INSERT INTO post_groups VALUES($0,$1)", id, group);
                if (publish || p?["published"] is not null) Portal.Audit(db, u, publish ? "news.publish" : "news.unpublish", id);
                return new { id };
            });
        });
        api.MapPost("/posts/{id:long}/unpublish", (long id, HttpContext c, Db db) => db.Write(() =>
        {
            var u = Portal.User(c);
            var p = Portal.Find(db, "SELECT p.*,u.roles FROM posts p JOIN users u ON u.id=p.author WHERE p.id=$0", id);
            Portal.Allow(p.N("author") == u.N("id") || (u.Has("admin") && p.Has("curator")));
            if (db.Run("UPDATE posts SET published=NULL WHERE id=$0 AND published IS NOT NULL", id) > 0)
                Portal.Audit(db, u, "news.unpublish", id);
            return Results.NoContent();
        }));

        api.MapGet("/chat/{peer:long}", (long peer, HttpContext c, Db db) =>
        {
            var u = Portal.User(c);
            Portal.Allow(Portal.Staff(u));
            var messages = db.All("""
                SELECT m.*,u.name authorName FROM messages m JOIN users u ON u.id=m.author
                WHERE (m.author=$0 AND m.recipient=$1) OR (m.author=$1 AND m.recipient=$0) ORDER BY m.id
                """, u.N("id"), peer);
            foreach (var message in messages.Where(m => m["status"] is not null))
                message["history"] = db.All("SELECT status,created FROM message_history WHERE messageId=$0 ORDER BY id", message.N("id"));
            return messages;
        });
        api.MapPost("/chat/{peer:long}", (long peer, MessageInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var u = Portal.User(c);
            Portal.Allow(Portal.Staff(u) && peer != u.N("id"));
            var recipient = Portal.Find(db, "SELECT * FROM users WHERE id=$0 AND active=1", peer);
            Portal.Allow(Portal.Staff(recipient));
            var body = Portal.Required(input.Body, "Сообщение", 4000);
            string? status = null;
            string? deadline = null;
            if (input.Deadline is not null)
            {
                Portal.Allow((u.Has("teacher") && recipient.Has("teacher")) || (u.Has("deputy") && (recipient.Has("teacher") || recipient.Has("deputy"))));
                if (!DateOnly.TryParseExact(input.Deadline, "yyyy-MM-dd", out var date)) throw new InputError("Укажите срок поручения.");
                deadline = date.ToString("yyyy-MM-dd");
                status = "Назначено";
            }
            var id = db.Insert("INSERT INTO messages(author,recipient,body,created,deadline,status) VALUES($0,$1,$2,$3,$4,$5)",
                u.N("id"), peer, body, Portal.Now(), deadline, status);
            if (status is not null) db.Run("INSERT INTO message_history(messageId,status,created) VALUES($0,$1,$2)", id, status, Portal.Now());
            return new { id };
        }));
        api.MapPut("/tasks/{id:long}", (long id, StatusInput input, HttpContext c, Db db) => db.Write(() =>
        {
            var u = Portal.User(c);
            var m = Portal.Find(db, "SELECT * FROM messages WHERE id=$0 AND status IS NOT NULL", id);
            Portal.Allow(Portal.Staff(u) && u.N("id") == m.N("recipient"));
            if (input.Status is not ("В работе" or "Выполнено")) throw new InputError("Выберите статус поручения.");
            if (m.S("status") != input.Status)
            {
                db.Run("UPDATE messages SET status=$0 WHERE id=$1", input.Status, id);
                db.Run("INSERT INTO message_history(messageId,status,created) VALUES($0,$1,$2)", id, input.Status, Portal.Now());
            }
            return Results.NoContent();
        }));
    }
}
record MaterialMove(int Direction);
record MaterialAccess(bool Restricted, long[]? Groups);
record SubjectInput(string Title, long[]? Groups);
record MessageInput(string Body, string? Deadline);
record StatusInput(string Status);