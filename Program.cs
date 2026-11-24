using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var data = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "data");
Directory.CreateDirectory(data);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
var dbPath = Path.Combine(data, "college.sqlite");
builder.Services.AddScoped(_ => new Db(dbPath));
builder.Services.AddSingleton<PasswordHasher<string>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "ggkeit";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Events.OnValidatePrincipal = context =>
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<Db>();
        var user = db.One("SELECT * FROM users WHERE id=$0 AND active=1", context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        if (user is null || user.S("stamp") != context.Principal!.FindFirstValue("stamp")) context.RejectPrincipal();
        else context.HttpContext.Items["user"] = user;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();
using (var db = new Db(dbPath))
{
    db.Run("CREATE TABLE IF NOT EXISTS migrations(version TEXT PRIMARY KEY)");
    foreach (var file in Directory.GetFiles(Path.Combine(app.Environment.ContentRootPath, "Migrations"), "*.sql").Order())
        db.Write(() =>
        {
            var version = Path.GetFileName(file);
            if (db.One("SELECT version FROM migrations WHERE version=$0", version) is null)
            {
                db.Run(File.ReadAllText(file));
                db.Run("INSERT INTO migrations VALUES($0)", version);
            }
            return 0;
        });
    if (args is ["backup", var destination])
    {
        if (File.Exists(destination)) throw new IOException("Файл резервной копии уже существует.");
        db.Backup(Path.GetFullPath(destination));
        return;
    }
    if (args is ["reset-admin-password"])
    {
        var resetPassword = builder.Configuration["ResetAdminPassword"];
        if (string.IsNullOrWhiteSpace(resetPassword) || resetPassword.Length > 256)
            throw new InvalidOperationException("Задайте ResetAdminPassword длиной от 1 до 256 символов.");
        var admin = db.One("SELECT * FROM users WHERE login='ADMIN' COLLATE NOCASE AND groupId IS NULL");
        if (admin is null || !admin.Has("sysadmin"))
            throw new InvalidOperationException("Учётная запись системного администратора admin не найдена.");
        db.Write(() =>
        {
            var hash = app.Services.GetRequiredService<PasswordHasher<string>>().HashPassword("", resetPassword);
            db.Run("UPDATE users SET password=$0,active=1,stamp=$1 WHERE id=$2",
                hash, Guid.NewGuid().ToString(), admin.N("id"));
            Portal.Audit(db, admin, "account.password-reset-local", admin.N("id"));
            return 0;
        });
        Console.WriteLine("Пароль admin изменён. Прежние сеансы прекращены.");
        return;
    }
    var password = builder.Configuration["BootstrapPassword"];
    if (!string.IsNullOrWhiteSpace(password) && db.One("SELECT id FROM users LIMIT 1") is not null)
        app.Logger.LogInformation("BootstrapPassword пропущен: база уже содержит пользователей. Для сброса admin используйте reset-admin-password.");
    if (!string.IsNullOrWhiteSpace(password) && db.One("SELECT id FROM users LIMIT 1") is null)
        db.Run("INSERT INTO users(name,login,password,roles) VALUES('Администратор','ADMIN',$0,'sysadmin,admin')",
            app.Services.GetRequiredService<PasswordHasher<string>>().HashPassword("", password));
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Для доступа нужен HTTPS.");
            return;
        }
        await next();
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["Sec-Fetch-Site"] is var site &&
            (site == "cross-site" || site == "same-site"))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    try { await next(); }
    catch (InputError error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
    catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode == 19)
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Запись конфликтует с существующими данными." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapPost("/login", async (Login input, HttpContext context, Db db, PasswordHasher<string> hasher) =>
{
    if (string.IsNullOrWhiteSpace(input.LoginName) || string.IsNullOrEmpty(input.Password) || input.LoginName.Length > 200 || input.Password.Length > 256) throw new InputError("Введите логин и пароль.");
    var matches = db.All("SELECT * FROM users WHERE login=$0 AND active=1", Portal.LoginName(input.LoginName))
        .Where(user => hasher.VerifyHashedPassword("", user.S("password"), input.Password) != PasswordVerificationResult.Failed).ToList();
    if (matches.Count != 1) throw new InputError("Проверьте логин и пароль или обратитесь к куратору.", 401);
    var current = matches[0];
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, current.S("id")), new("stamp", current.S("stamp"))], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).AllowAnonymous().RequireRateLimiting("login");
api.MapPost("/logout", async (HttpContext c) => await c.SignOutAsync());
api.MapGet("/me", (HttpContext c) =>
{
    var u = Portal.User(c);
    return new { id = u.N("id"), name = u.S("name"), roles = u.S("roles").Split(','), groupId = u.N("groupId") };
});
Portal.Map(api);
Schedule.Map(api);
Content.Map(api);
app.Run();

record Login(string LoginName, string Password);
sealed class InputError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}


