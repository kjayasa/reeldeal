using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace reeldeel_new.Cms;

public static partial class AdminEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ContentKeyPattern();

    // A plain Handlebars identifier, so {{name}} resolves it.
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex VariableNamePattern();

    public const int MinPasswordLength = 8;

    public sealed record ContentUpdate(string Markdown);
    public sealed record TemplateUpdate(string Source);
    public sealed record VariableUpdate(string Value, string? Description);
    public sealed record NewUser(string Email, string? Password);
    public sealed record PasswordUpdate(string Password);

    public static void MapAdmin(this WebApplication app, string adminHtmlPath)
    {
        // --- Public (anonymous) auth pages & flow ---

        app.MapGet("/admin/login", (HttpContext ctx) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/admin");
            var error = ctx.Request.Query["error"].ToString();
            var returnUrl = SafeReturnUrl(ctx.Request.Query["ReturnUrl"].ToString());
            return Results.Content(LoginPage(error, returnUrl), "text/html");
        }).AllowAnonymous();

        app.MapPost("/admin/login", async (HttpContext ctx, CmsRepository repo) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

            var canonicalEmail = repo.Authenticate(email, password);
            if (canonicalEmail is null)
            {
                var back = "/admin/login?error=invalid";
                if (returnUrl != "/admin") back += "&ReturnUrl=" + WebUtility.UrlEncode(returnUrl);
                return Results.Redirect(back);
            }

            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Email, canonicalEmail));
            identity.AddClaim(new Claim(ClaimTypes.Name, canonicalEmail));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = form["remember"] == "on" });
            return Results.Redirect(returnUrl);
        }).AllowAnonymous();

        app.MapPost("/admin/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/admin/login");
        }).AllowAnonymous();

        // Reached when a signed-in user's account has since been removed from the Users table.
        app.MapGet("/admin/denied", (HttpContext ctx) =>
        {
            var email = ctx.User.FindFirstValue(ClaimTypes.Email) ?? "unknown";
            return Results.Content(DeniedPage(email), "text/html");
        }).AllowAnonymous();

        // --- Admin SPA + API (require a registered user) ---

        var admin = app.MapGroup("").RequireAuthorization(AdminUserHandler.PolicyName);

        admin.MapGet("/admin", () =>
            File.Exists(adminHtmlPath)
                ? Results.Content(File.ReadAllText(adminHtmlPath), "text/html")
                : Results.NotFound("admin/index.html not found"));

        admin.MapGet("/admin/api/me", (HttpContext ctx) => Results.Ok(new
        {
            email = CurrentUser(ctx),
            name = ctx.User.Identity?.Name
        }));

        // Content blocks
        admin.MapGet("/admin/api/content", (CmsRepository repo) => Results.Ok(repo.GetContentBlocks()));

        // Creates the block if the key is new, otherwise updates it.
        admin.MapPut("/admin/api/content/{key}", (string key, ContentUpdate body, HttpContext ctx, CmsRepository repo, SiteRenderer renderer) =>
        {
            if (!ContentKeyPattern().IsMatch(key))
                return Results.BadRequest("Key must start with a letter or digit and contain only letters, digits, '-' or '_'");

            var markdown = body.Markdown ?? "";
            if (renderer.Validate(contentKey: key, contentMarkdown: markdown) is { } error)
                return Results.BadRequest($"Template error: {error}");

            repo.SaveContent(key, markdown, CurrentUser(ctx));
            renderer.Invalidate();
            return Results.Ok();
        });

        admin.MapDelete("/admin/api/content/{key}", (string key, CmsRepository repo, SiteRenderer renderer) =>
        {
            if (!repo.DeleteContent(key)) return Results.NotFound();
            renderer.Invalidate();
            return Results.Ok();
        });

        // Page template (the whole index Handlebars template)
        admin.MapGet("/admin/api/template", (CmsRepository repo) =>
            Results.Ok(repo.GetTemplate(SiteRenderer.IndexTemplateName)
                       ?? new PageTemplate(SiteRenderer.IndexTemplateName, "", "", null)));

        admin.MapPut("/admin/api/template", (TemplateUpdate body, HttpContext ctx, CmsRepository repo, SiteRenderer renderer) =>
        {
            var source = body.Source ?? "";
            if (renderer.Validate(indexSource: source) is { } error)
                return Results.BadRequest($"Template error: {error}");

            repo.SaveTemplate(SiteRenderer.IndexTemplateName, source, CurrentUser(ctx));
            renderer.Invalidate();
            return Results.Ok();
        });

        // Template variables ({{name}} in the page template and content blocks)
        admin.MapGet("/admin/api/variables", (CmsRepository repo) => Results.Ok(repo.GetVariables()));

        // Creates the variable if the name is new, otherwise updates it.
        admin.MapPut("/admin/api/variables/{name}", (string name, VariableUpdate body, HttpContext ctx, CmsRepository repo, SiteRenderer renderer) =>
        {
            if (!VariableNamePattern().IsMatch(name))
                return Results.BadRequest("Name must start with a letter or '_' and contain only letters, digits or '_'");
            if (string.Equals(name, "all_data", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("'all_data' is reserved");

            repo.SaveVariable(name, body.Value ?? "", string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(), CurrentUser(ctx));
            renderer.Invalidate();
            return Results.Ok();
        });

        admin.MapDelete("/admin/api/variables/{name}", (string name, CmsRepository repo, SiteRenderer renderer) =>
        {
            if (!repo.DeleteVariable(name)) return Results.NotFound();
            renderer.Invalidate();
            return Results.Ok();
        });

        // Users
        admin.MapGet("/admin/api/users", (CmsRepository repo) => Results.Ok(repo.GetUsers()));

        admin.MapPost("/admin/api/users", (NewUser body, CmsRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email)) return Results.BadRequest("Email is required");
            if (!string.IsNullOrEmpty(body.Password) && body.Password.Length < MinPasswordLength)
                return Results.BadRequest($"Password must be at least {MinPasswordLength} characters");
            repo.AddUser(body.Email, body.Password);
            return Results.Ok();
        });

        admin.MapPut("/admin/api/users/{email}/password", (string email, PasswordUpdate body, CmsRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest("Email is required");
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length < MinPasswordLength)
                return Results.BadRequest($"Password must be at least {MinPasswordLength} characters");
            repo.SetPassword(email, body.Password);
            return Results.Ok();
        });

        admin.MapDelete("/admin/api/users/{email}", (string email, CmsRepository repo) =>
        {
            // Don't let an admin lock everyone out by deleting the last account that can sign in.
            var target = repo.GetUsers().FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
            if (target is null) return Results.NotFound();
            if (target.HasPassword && repo.CountUsersWithPassword() <= 1)
                return Results.BadRequest("Cannot remove the last admin who can sign in");
            repo.RemoveUser(email);
            return Results.Ok();
        });
    }

    private static string? CurrentUser(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.Identity?.Name;

    /// <summary>Only allow same-site relative redirects after login (never an absolute URL
    /// or protocol-relative "//host" that could bounce the user somewhere else).</summary>
    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
            ? url
            : "/admin";

    private static string LoginPage(string? error, string returnUrl)
    {
        var message = error switch
        {
            "invalid" => "<p class='err'>Incorrect email or password.</p>",
            _ => ""
        };

        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>ReelDeal Admin · Sign in</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
              body{font-family:system-ui,Segoe UI,Arial,sans-serif;background:#111;color:#eee;display:flex;
                   min-height:100vh;align-items:center;justify-content:center;margin:0}
              .card{background:#1d1d1d;padding:2.5rem;border-radius:12px;min-width:320px;
                    box-shadow:0 10px 40px rgba(0,0,0,.5)}
              h1{font-size:1.3rem;margin:0 0 1.5rem;text-align:center}
              label{display:block;color:#999;font-size:.8rem;margin:.9rem 0 .3rem}
              input[type=email],input[type=password]{width:100%;box-sizing:border-box;padding:.6rem;border-radius:8px;
                   border:1px solid #333;background:#0d0d0d;color:#eee;font:inherit}
              .remember{display:flex;align-items:center;gap:.5rem;margin:1rem 0;color:#999;font-size:.85rem}
              .btn{display:block;width:100%;margin-top:.5rem;padding:.8rem 1.2rem;background:#00B3FE;color:#fff;
                   border:0;border-radius:8px;font:inherit;font-size:1rem;font-weight:600;cursor:pointer}
              .btn:hover{background:#0090cc}
              .err{color:#e66;margin:0;text-align:center}
            </style></head><body>
              <div class="card">
                <h1>ReelDeal Admin</h1>
                {{message}}
                <form method="post" action="/admin/login">
                  <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl)}}">
                  <label for="email">Email</label>
                  <input id="email" type="email" name="email" placeholder="name@example.com" required autofocus autocomplete="username">
                  <label for="password">Password</label>
                  <input id="password" type="password" name="password" required autocomplete="current-password">
                  <label class="remember"><input type="checkbox" name="remember"> Stay signed in</label>
                  <button type="submit" class="btn">Sign in</button>
                </form>
              </div>
            </body></html>
            """;
    }

    private static string DeniedPage(string email) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Not authorized</title>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>body{font-family:system-ui,Segoe UI,Arial,sans-serif;background:#111;color:#eee;display:flex;
          min-height:100vh;align-items:center;justify-content:center;margin:0;text-align:center}
          .card{background:#1d1d1d;padding:2.5rem;border-radius:12px;max-width:420px}
          form{margin-top:1rem}button{padding:.6rem 1.2rem;border:0;border-radius:8px;background:#444;color:#fff;cursor:pointer}</style>
        </head><body><div class="card">
          <h1>Not authorized</h1>
          <p>You're signed in as <strong>{{WebUtility.HtmlEncode(email)}}</strong>, but this account is no longer an admin.</p>
          <form method="post" action="/admin/logout"><button type="submit">Sign out</button></form>
        </div></body></html>
        """;
}
