namespace reeldeel_new.Cms;

/// <summary>
/// Maintenance commands run as <c>dotnet run -- &lt;command&gt;</c>. Each one works on the
/// database named by the Cms connection string and exits without starting the web server.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        Maintenance commands (run from the project directory):
          dotnet run -- --init-db
              Create/migrate the database schema and seed content from /data.
          dotnet run -- --set-password <email> [password]
              Create the admin if needed and set their password.
          dotnet run -- --reset-admin <email> [password]
              Remove ALL admins and leave exactly one with this password.
              Use before committing cms.db so the seed has one known login.
        If [password] is omitted it is read from stdin (first line), which keeps it out of
        the command line and shell history. Passwords must be at least 8 characters.
        """;

    /// <summary>Returns true if <paramref name="args"/> named a maintenance command (the
    /// caller should then exit). Sets Environment.ExitCode on failure.</summary>
    public static bool TryRun(string[] args, CmsRepository repo, string connectionString)
    {
        if (args.Length == 0) return false;

        switch (args[0])
        {
            case "--init-db":
                Console.WriteLine($"CMS database initialized at connection: {connectionString}");
                return true;

            case "--set-password":
                return RunWithCredentials(args, (email, password) =>
                {
                    repo.SetPassword(email, password);
                    Console.WriteLine($"Password set for {email}");
                });

            case "--reset-admin":
                return RunWithCredentials(args, (email, password) =>
                {
                    repo.ResetToSingleAdmin(email, password);
                    Console.WriteLine($"Users table reset: {email} is now the only admin.");
                });

            case "--help":
            case "-h":
                Console.WriteLine(Usage);
                return true;

            default:
                return false;
        }
    }

    private static bool RunWithCredentials(string[] args, Action<string, string> action)
    {
        var email = args.Length > 1 ? args[1].Trim() : "";
        var password = args.Length > 2 ? args[2] : ReadPasswordFromStdin();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Fail("A valid email is required.");
        if (password is null || password.Length < AdminEndpoints.MinPasswordLength)
            return Fail($"Password must be at least {AdminEndpoints.MinPasswordLength} characters.");

        action(email, password);
        return true;
    }

    private static string? ReadPasswordFromStdin()
    {
        if (!Console.IsInputRedirected)
            Console.Error.Write("Password: ");
        return Console.ReadLine()?.TrimEnd('\r', '\n');
    }

    private static bool Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        Environment.ExitCode = 1;
        return true;
    }
}
