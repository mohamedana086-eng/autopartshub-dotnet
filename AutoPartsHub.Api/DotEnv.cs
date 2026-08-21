namespace AutoPartsHub.Api;

/// <summary>
/// Reads a <c>.env</c> file next to the project into the process environment.
/// </summary>
/// <remarks>
/// Development only, and deliberately not a package. In a deployment the host
/// sets real environment variables and this finds no file and does nothing;
/// locally it means <c>dotnet run</c> works without anyone having to remember
/// an export, which is the step that otherwise gets done wrong once and points
/// a migration at the wrong database.
///
/// Anything already set wins, so a variable passed on the command line beats
/// the file. A line without an <c>=</c> is skipped rather than guessed at.
/// </remarks>
public static class DotEnv
{
    public static void Load(string directory)
    {
        var path = Path.Combine(directory, ".env");
        if (!File.Exists(path)) return;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq < 1) continue;

            var key = line[..eq].Trim();
            if (Environment.GetEnvironmentVariable(key) is not null) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length > 1 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
            {
                value = value[1..^1];
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
