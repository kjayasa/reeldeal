using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using reeldeel_new.Cms;

var builder = WebApplication.CreateBuilder(args);

var contentRoot = builder.Environment.ContentRootPath;
var publicDir = Path.Combine(contentRoot, "public");
var dataDir = Path.Combine(contentRoot, "data");
var adminHtmlPath = Path.Combine(contentRoot, "admin", "index.html");

// --- CMS data layer (Dapper + SQLite; connection string lives in app config) ---
var connectionString = builder.Configuration.GetConnectionString("Cms")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Cms in app config.");
var repo = new CmsRepository(connectionString);
var initialAdmin = builder.Configuration.GetSection("Cms:InitialAdmin");
repo.InitializeAndSeed(dataDir, initialAdmin["Email"], initialAdmin["Password"]);
builder.Services.AddSingleton(repo);

// Maintenance commands (`dotnet run -- --init-db | --set-password | --reset-admin`)
// operate on the database and exit without starting the web server. See AdminCli.Usage.
if (AdminCli.TryRun(args, repo, connectionString))
    return;

if (repo.CountUsersWithPassword() == 0)
{
    Console.Error.WriteLine(
        "WARNING: no admin user has a password, so nobody can sign in to /admin. " +
        "Set Cms:InitialAdmin:Email and Cms:InitialAdmin:Password in app config (e.g. the " +
        "Cms__InitialAdmin__Password environment variable) and restart, or run: dotnet run -- --set-password <email> <password>");
}

// --- Site renderer (index template, content blocks and variables all from the DB; cached) ---
var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(publicDir, "manifest.json"))).RootElement
    .EnumerateObject()
    .ToDictionary(p => p.Name, p => p.Value.GetString()!);
var renderer = new SiteRenderer(repo, manifest);
builder.Services.AddSingleton(renderer);

// --- Authentication: cookie issued by the email/password login form (/admin/login). ---
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Cookie.Name = "reeldeel.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddSingleton<IAuthorizationHandler, AdminUserHandler>();
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AdminUserHandler.PolicyName, p =>
    {
        p.RequireAuthenticatedUser();
        p.Requirements.Add(new AdminUserRequirement());
    }));

var app = builder.Build();

var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".js", ".css",
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".webp",
    ".woff", ".woff2", ".ttf", ".eot", ".otf"
};

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publicDir),
    OnPrepareResponse = ctx =>
    {
        var ext = Path.GetExtension(ctx.File.Name);
        if (!allowedExtensions.Contains(ext))
        {
            ctx.Context.Response.StatusCode = 404;
            ctx.Context.Response.ContentLength = 0;
            ctx.Context.Response.Body = Stream.Null;
        }
    }
});

app.UseAuthentication();
app.UseAuthorization();

// --- Public site (rendered from the CMS database) ---
app.MapGet("/", (SiteRenderer r) => Results.Content(r.Render(), "text/html"));

// --- Admin pages + API ---
app.MapAdmin(adminHtmlPath);

app.Run();
