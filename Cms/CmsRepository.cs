using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace reeldeel_new.Cms;

public sealed record ContentBlock(string Key, string Markdown, string UpdatedAt, string? UpdatedBy);
public sealed record TemplateVariable(string Name, string Value, string? Description, string UpdatedAt, string? UpdatedBy);
public sealed record PageTemplate(string Name, string Source, string UpdatedAt, string? UpdatedBy);
public sealed record AdminUser(string Email, string AddedAt, bool HasPassword, string? PasswordSetAt);

/// <summary>
/// All CMS persistence. Uses Dapper over a SQLite file whose path comes from the
/// "Cms" connection string in app config. The .db file is committed to the repo as
/// the seed/starting state and copied next to the app on build, so edits made through
/// the admin pages are written straight back to that file at runtime.
/// </summary>
public sealed class CmsRepository
{
    // Verified against when a login names an unknown email so the response time doesn't
    // reveal whether an account exists.
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    private readonly string _connectionString;

    public CmsRepository(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string Now() => DateTime.UtcNow.ToString("o");

    /// <summary>Creates the schema if needed, migrates the legacy OAuth allow-list into
    /// the Users table and the legacy JSON variables into rows, and seeds content/
    /// variables/templates from the on-disk /data files the first time each is missing. If no user can sign in yet and an initial admin
    /// email + password are configured, that account is created/updated so the first
    /// deploy is never locked out.</summary>
    public void InitializeAndSeed(string seedDataDir, string? initialAdminEmail, string? initialAdminPassword)
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS ContentBlocks (
                Key       TEXT PRIMARY KEY,
                Markdown  TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UpdatedBy TEXT
            );
            CREATE TABLE IF NOT EXISTS Templates (
                Name      TEXT PRIMARY KEY,
                Source    TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UpdatedBy TEXT
            );
            CREATE TABLE IF NOT EXISTS Users (
                Email         TEXT PRIMARY KEY COLLATE NOCASE,
                PasswordHash  TEXT,
                AddedAt       TEXT NOT NULL,
                PasswordSetAt TEXT
            );
            """);

        // Migrate from the previous social-login allow-list: keep the emails (they'll need
        // a password set before they can sign in) and drop the old table.
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AllowedUsers'") > 0)
        {
            conn.Execute("""
                INSERT OR IGNORE INTO Users (Email, AddedAt) SELECT Email, AddedAt FROM AllowedUsers;
                DROP TABLE AllowedUsers;
                """);
        }

        var now = Now();

        // Variables used to be a single JSON blob (row Id = 1). Move that table aside, create
        // the one-row-per-variable table, and copy the blob across as individual rows.
        using (var tx = conn.BeginTransaction())
        {
            string? legacyVariablesJson = null;
            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('Variables') WHERE name = 'Json'", transaction: tx) > 0)
            {
                legacyVariablesJson = conn.ExecuteScalar<string>("SELECT Json FROM Variables WHERE Id = 1", transaction: tx) ?? "{}";
                conn.Execute("DROP TABLE Variables", transaction: tx);
            }
            conn.Execute("""
                CREATE TABLE IF NOT EXISTS Variables (
                    Name        TEXT PRIMARY KEY,
                    Value       TEXT NOT NULL,
                    Description TEXT,
                    UpdatedAt   TEXT NOT NULL,
                    UpdatedBy   TEXT
                );
                """, transaction: tx);

            if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Variables", transaction: tx) == 0)
            {
                var dataJsonPath = Path.Combine(seedDataDir, "data.json");
                var json = legacyVariablesJson ?? (File.Exists(dataJsonPath) ? File.ReadAllText(dataJsonPath) : "{}");
                var by = legacyVariablesJson is null ? "seed" : "migrated";
                foreach (var (name, value) in FlattenJson(json))
                {
                    conn.Execute(
                        "INSERT OR IGNORE INTO Variables (Name, Value, UpdatedAt, UpdatedBy) VALUES (@Name, @Value, @UpdatedAt, @By)",
                        new { Name = name, Value = value, UpdatedAt = now, By = by }, tx);
                }
            }
            tx.Commit();
        }

        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ContentBlocks") == 0 && Directory.Exists(seedDataDir))
        {
            foreach (var file in Directory.GetFiles(seedDataDir, "*.md"))
            {
                conn.Execute(
                    "INSERT INTO ContentBlocks (Key, Markdown, UpdatedAt, UpdatedBy) VALUES (@Key, @Markdown, @UpdatedAt, 'seed')",
                    new { Key = Path.GetFileNameWithoutExtension(file), Markdown = File.ReadAllText(file), UpdatedAt = now });
            }
        }

        // Each named template (e.g. "index" <- data/index.hbs) is seeded independently, so a
        // database created before templates moved into the DB picks them up on next start.
        if (Directory.Exists(seedDataDir))
        {
            foreach (var file in Directory.GetFiles(seedDataDir, "*.hbs"))
            {
                conn.Execute(
                    "INSERT OR IGNORE INTO Templates (Name, Source, UpdatedAt, UpdatedBy) VALUES (@Name, @Source, @UpdatedAt, 'seed')",
                    new { Name = Path.GetFileNameWithoutExtension(file), Source = File.ReadAllText(file), UpdatedAt = now });
            }
        }

        // Bootstrap: only while nobody has a password, so a configured initial password
        // can't silently overwrite one that was later changed through the admin UI.
        if (CountUsersWithPassword(conn) == 0
            && !string.IsNullOrWhiteSpace(initialAdminEmail)
            && !string.IsNullOrWhiteSpace(initialAdminPassword))
        {
            UpsertPassword(conn, initialAdminEmail, initialAdminPassword);
        }
    }

    // --- Content blocks ---

    public IReadOnlyList<ContentBlock> GetContentBlocks()
    {
        using var conn = Open();
        return conn.Query<ContentBlock>(
            "SELECT Key, Markdown, UpdatedAt, UpdatedBy FROM ContentBlocks ORDER BY Key").AsList();
    }

    public Dictionary<string, string> GetContentMap()
    {
        using var conn = Open();
        return conn.Query<(string Key, string Markdown)>("SELECT Key, Markdown FROM ContentBlocks")
                   .ToDictionary(r => r.Key, r => r.Markdown, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveContent(string key, string markdown, string? user)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO ContentBlocks (Key, Markdown, UpdatedAt, UpdatedBy)
            VALUES (@Key, @Markdown, @UpdatedAt, @UpdatedBy)
            ON CONFLICT(Key) DO UPDATE SET Markdown = @Markdown, UpdatedAt = @UpdatedAt, UpdatedBy = @UpdatedBy;
            """, new { Key = key, Markdown = markdown, UpdatedAt = Now(), UpdatedBy = user });
    }

    /// <summary>Deletes a content block. Returns false if no block had that key.</summary>
    public bool DeleteContent(string key)
    {
        using var conn = Open();
        return conn.Execute("DELETE FROM ContentBlocks WHERE Key = @Key", new { Key = key }) > 0;
    }

    // --- Page templates (Handlebars sources, e.g. "index" for the home page) ---

    public PageTemplate? GetTemplate(string name)
    {
        using var conn = Open();
        return conn.QuerySingleOrDefault<PageTemplate>(
            "SELECT Name, Source, UpdatedAt, UpdatedBy FROM Templates WHERE Name = @Name", new { Name = name });
    }

    public void SaveTemplate(string name, string source, string? user)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO Templates (Name, Source, UpdatedAt, UpdatedBy)
            VALUES (@Name, @Source, @UpdatedAt, @UpdatedBy)
            ON CONFLICT(Name) DO UPDATE SET Source = @Source, UpdatedAt = @UpdatedAt, UpdatedBy = @UpdatedBy;
            """, new { Name = name, Source = source, UpdatedAt = Now(), UpdatedBy = user });
    }

    // --- Template variables (one row per {{name}}) ---

    public IReadOnlyList<TemplateVariable> GetVariables()
    {
        using var conn = Open();
        return conn.Query<TemplateVariable>(
            "SELECT Name, Value, Description, UpdatedAt, UpdatedBy FROM Variables ORDER BY Name").AsList();
    }

    public Dictionary<string, string> GetVariableMap()
    {
        using var conn = Open();
        return conn.Query<(string Name, string Value)>("SELECT Name, Value FROM Variables")
                   .ToDictionary(r => r.Name, r => r.Value);
    }

    public void SaveVariable(string name, string value, string? description, string? user)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO Variables (Name, Value, Description, UpdatedAt, UpdatedBy)
            VALUES (@Name, @Value, @Description, @UpdatedAt, @UpdatedBy)
            ON CONFLICT(Name) DO UPDATE SET Value = @Value, Description = @Description, UpdatedAt = @UpdatedAt, UpdatedBy = @UpdatedBy;
            """, new { Name = name, Value = value, Description = description, UpdatedAt = Now(), UpdatedBy = user });
    }

    /// <summary>Deletes a variable. Returns false if none had that name.</summary>
    public bool DeleteVariable(string name)
    {
        using var conn = Open();
        return conn.Execute("DELETE FROM Variables WHERE Name = @Name", new { Name = name }) > 0;
    }

    /// <summary>Turns a JSON object into flat name/value pairs for the Variables table.
    /// Nested objects are joined with "_" ({"a":{"b":1}} -> a_b = "1"); strings are
    /// unquoted and any other value keeps its JSON text.</summary>
    private static IEnumerable<(string Name, string Value)> FlattenJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return [];
        var result = new List<(string, string)>();
        Flatten(doc.RootElement, "");
        return result;

        void Flatten(JsonElement obj, string prefix)
        {
            foreach (var p in obj.EnumerateObject())
            {
                var name = prefix + p.Name;
                if (p.Value.ValueKind == JsonValueKind.Object)
                    Flatten(p.Value, name + "_");
                else
                    result.Add((name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
            }
        }
    }

    // --- Users / authentication ---

    /// <summary>True if the email belongs to a registered admin (with or without a
    /// password). Used by the per-request authorization check so that removing a user
    /// signs them out on their next request.</summary>
    public bool UserExists(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        using var conn = Open();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM Users WHERE Email = @Email", new { Email = email.Trim() }) > 0;
    }

    /// <summary>Verifies an email/password pair. Returns the canonical (stored) email on
    /// success, or null. Always performs a hash comparison so unknown emails and wrong
    /// passwords take the same time.</summary>
    public string? Authenticate(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return null;

        using var conn = Open();
        var row = conn.QuerySingleOrDefault<(string Email, string? PasswordHash)>(
            "SELECT Email, PasswordHash FROM Users WHERE Email = @Email", new { Email = email.Trim() });

        var ok = PasswordHasher.Verify(password, row.PasswordHash ?? DummyHash) && row.PasswordHash is not null;
        return ok ? row.Email : null;
    }

    public IReadOnlyList<AdminUser> GetUsers()
    {
        using var conn = Open();
        // Hash is only used to derive HasPassword; it never leaves this method.
        return conn.Query<(string Email, string AddedAt, string? PasswordHash, string? PasswordSetAt)>(
                "SELECT Email, AddedAt, PasswordHash, PasswordSetAt FROM Users ORDER BY Email")
            .Select(r => new AdminUser(r.Email, r.AddedAt, r.PasswordHash is not null, r.PasswordSetAt))
            .ToList();
    }

    public int CountUsersWithPassword()
    {
        using var conn = Open();
        return CountUsersWithPassword(conn);
    }

    private static int CountUsersWithPassword(SqliteConnection conn) =>
        (int)conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE PasswordHash IS NOT NULL");

    /// <summary>Adds a user. A null/empty password creates the account without one; they
    /// won't be able to sign in until an admin sets a password.</summary>
    public void AddUser(string email, string? password)
    {
        using var conn = Open();
        if (string.IsNullOrEmpty(password))
        {
            conn.Execute(
                "INSERT OR IGNORE INTO Users (Email, AddedAt) VALUES (@Email, @AddedAt)",
                new { Email = email.Trim(), AddedAt = Now() });
        }
        else
        {
            UpsertPassword(conn, email, password);
        }
    }

    /// <summary>Sets (or resets) a user's password, creating the user if needed.</summary>
    public void SetPassword(string email, string password)
    {
        using var conn = Open();
        UpsertPassword(conn, email, password);
    }

    private static void UpsertPassword(SqliteConnection conn, string email, string password)
    {
        var now = Now();
        conn.Execute("""
            INSERT INTO Users (Email, PasswordHash, AddedAt, PasswordSetAt)
            VALUES (@Email, @PasswordHash, @Now, @Now)
            ON CONFLICT(Email) DO UPDATE SET PasswordHash = @PasswordHash, PasswordSetAt = @Now;
            """, new { Email = email.Trim(), PasswordHash = PasswordHasher.Hash(password), Now = now });
    }

    /// <summary>Replaces every user with a single admin account holding the given password.
    /// Used to prepare the committed seed database.</summary>
    public void ResetToSingleAdmin(string email, string password)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM Users", transaction: tx);
        var now = Now();
        conn.Execute(
            "INSERT INTO Users (Email, PasswordHash, AddedAt, PasswordSetAt) VALUES (@Email, @PasswordHash, @Now, @Now)",
            new { Email = email.Trim(), PasswordHash = PasswordHasher.Hash(password), Now = now }, tx);
        tx.Commit();
    }

    public void RemoveUser(string email)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM Users WHERE Email = @Email", new { Email = email.Trim() });
    }
}
