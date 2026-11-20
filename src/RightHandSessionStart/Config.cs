using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace RightHandSessionStart;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>One part of the briefing. Kind is "command", "files" or "list"; the rest depends on the kind.</summary>
public sealed class Section
{
    public required string Kind { get; init; }
    public required string Name { get; init; }

    // command
    public IReadOnlyList<string> Run { get; init; } = [];
    public IReadOnlyList<int> OkExit { get; init; } = [0];
    public string Heading { get; init; } = "inline";   // inline, above, each, none
    public IReadOnlyList<string> DropLines { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Any kind: "always", or "away" to show it only in the first session after time away (0.4.0).</summary>
    public string When { get; init; } = "always";

    // files
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyDictionary<string, string> Where { get; init; } = new Dictionary<string, string>();

    /// <summary>Only files whose name's date is on or after the previous session's day (0.4.0).</summary>
    public bool Since { get; init; }

    // list
    public string? File { get; init; }
    public string? Under { get; init; }
    public string Show { get; init; } = "count";   // count, list
    public string Text { get; init; } = "";
}

/// <summary>The settings file. See the README, and Template. Read strictly: an unknown key or a value of the wrong
/// type is an error that names it.</summary>
public sealed class Config
{
    public const string EnvConfig = "RIGHT_HAND_SESSION_START_CONFIG";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# right-hand-session-start settings. See the README.\n" +
        "# base = \"base-config.toml\"     # settings shared with other tools\n" +
        "# root = \"..\"                   # file paths are shown relative to it (default: this folder)\n" +
        "# skip_env = [\"CLAUDE_HEADLESS\"] # hook: say nothing when one of these is set (a tool's headless run)\n" +
        "# visible = \"headlines\"        # hook: what you see on screen: headlines, full or none (Claude gets it all)\n" +
        "# state_file = \"state.json\"     # where each session's start is recorded (default: %LOCALAPPDATA%\\right-hand-session-start)\n" +
        "\n" +
        "# [away]\n" +
        "# after_working_days = 3        # the first session after this many weekdays without one is \"away\"\n" +
        "\n" +
        "[[section]]\n" +
        "kind = \"command\"                # run a program, show what it prints\n" +
        "name = \"inbox due\"\n" +
        "run = [\"md-inbox.exe\", \"due\"]\n" +
        "heading = \"each\"                # inline, above, each or none\n" +
        "drop_lines = [\"Nothing due.\"]   # lines that mean there's nothing to say\n";

    public static readonly string[] KnownKeys = ["root", "skip_env", "title", "visible", "footer", "section", "state_file", "away"];

    /// <summary>Where the hook records each interactive session's start (0.4.0).</summary>
    public required string StateFile { get; init; }

    /// <summary>[away] after_working_days: the first session after at least this many weekdays without one is in away
    /// mode (0.4.0).</summary>
    public int AwayAfterWorkingDays { get; init; } = 3;

    public required string Path { get; init; }
    public required string Root { get; init; }
    public string Title { get; init; } = "Session start, {now}";

    /// <summary>hook: what the person sees on screen, as well as Claude getting the whole briefing: "headlines" (a
    /// line per section, 0.3.0), "full" (the briefing, 0.2.0's true) or "none".</summary>
    public string Visible { get; init; } = "headlines";

    /// <summary>hook: a line under the headlines (what to do for the rest, say).</summary>
    public string Footer { get; init; } = "";
    public IReadOnlyList<string> SkipEnv { get; init; } = ["CLAUDE_HEADLESS"];
    public IReadOnlyList<Section> Sections { get; init; } = [];

    /// <summary>A path setting as a full path: ~ and %VARIABLES% (the base's [paths] first) expanded, relative to the
    /// settings file's folder.</summary>
    public required Func<string, string> Resolve { get; init; }

    /// <summary>A command's argument: variables expanded; a path only if it was a path to begin with.</summary>
    public required Func<string, string> Expand { get; init; }

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData,
            "right-hand-session-start", "config.toml"));
    }

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!System.IO.File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `right-hand-session-start config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");

        var dir = System.IO.Path.GetDirectoryName(full)!;
        var vars = env;
        string Expand(string value) => PyPath.ExpandVars(PyPath.ExpandUser(value, vars), vars);
        string Resolve(string value)
        {
            var expanded = Expand(value).Replace('/', '\\');
            return PyPath.Absolute(PyPath.IsAbsolute(expanded) ? expanded : System.IO.Path.Join(dir, expanded));
        }
        UsageException Bad(string where, string what) => new($"{full}: {where} {what}.");
        string Text(TomlTable table, string key, string where, string? fallback = null) =>
            table.TryGetValue(key, out var value)
                ? value is string { Length: > 0 } s ? s : throw Bad(where, $"{key} should be text, and not empty")
                : fallback ?? throw Bad(where, $"needs {key}");
        List<string> Texts(TomlTable table, string key, string where, bool required = false)
        {
            if (!table.TryGetValue(key, out var value))
                return required ? throw Bad(where, $"needs {key}") : [];
            return value switch
            {
                string one => [one],
                TomlArray many when many.All(x => x is string) => many.Cast<string>().ToList(),
                _ => throw Bad(where, $"{key} should be a list of text"),
            };
        }

        var sections = new List<Section>();
        if (raw.TryGetValue("section", out var s))
        {
            if (s is not TomlTableArray array)
                throw new UsageException($"{full}: section should be [[section]] tables.");
            foreach (var table in array)
            {
                var where = $"[[section]] {(table.TryGetValue("name", out var n) ? n : "(no name)")}";
                var kind = Text(table, "kind", where);
                string[] keys = kind switch
                {
                    "command" => ["kind", "name", "when", "run", "ok_exit", "heading", "drop_lines", "timeout_seconds"],
                    "files" => ["kind", "name", "when", "files", "where", "since"],
                    "list" => ["kind", "name", "when", "file", "under", "show", "text"],
                    _ => throw Bad(where, $"kind should be command, files or list, not '{kind}'"),
                };
                foreach (var key in table.Keys)
                    if (!keys.Contains(key))
                        throw Bad(where, $"has an unknown setting '{key}' (a {kind} section knows: {string.Join(", ", keys.Skip(1))})");
                var name = Text(table, "name", where);
                var when = Text(table, "when", where, "always");
                if (when is not ("always" or "away"))
                    throw Bad(where, $"when should be always or away, not '{when}'");
                switch (kind)
                {
                    case "command":
                        var run = Texts(table, "run", where, required: true);
                        if (run.Count == 0)
                            throw Bad(where, "run should name a program");
                        var heading = Text(table, "heading", where, "inline");
                        if (heading is not ("inline" or "above" or "each" or "none"))
                            throw Bad(where, $"heading should be inline, above, each or none, not '{heading}'");
                        var ok = table.TryGetValue("ok_exit", out var o)
                            ? o is TomlArray codes && codes.All(c => c is long) ? codes.Select(c => (int)(long)c!).ToList() : throw Bad(where, "ok_exit should be a list of exit codes")
                            : [0];
                        var timeout = table.TryGetValue("timeout_seconds", out var t)
                            ? t is long secs && secs is >= 1 and <= 600 ? (int)secs : throw Bad(where, "timeout_seconds should be 1 to 600")
                            : 120;
                        sections.Add(new Section
                        {
                            Kind = kind, Name = name, When = when, Heading = heading, OkExit = ok, TimeoutSeconds = timeout,
                            // An argument with a slash is a path, relative to this file; anything else (a program
                            // on the PATH, an option, a word) only has its variables expanded.
                            Run = run.Select(a => a.Contains('/') || a.Contains('\\') ? Resolve(a) : Expand(a)).ToList(),
                            DropLines = Texts(table, "drop_lines", where),
                        });
                        break;
                    case "files":
                        var match = new Dictionary<string, string>();
                        if (table.TryGetValue("where", out var w))
                        {
                            if (w is not TomlTable wt || wt.Values.Any(v => v is not string))
                                throw Bad(where, "where should be a table of frontmatter field = value");
                            foreach (var (key, value) in wt)
                                match[key] = (string)value!;
                        }
                        var since = table.TryGetValue("since", out var si) ? si as bool? ?? throw Bad(where, "since should be true or false") : false;
                        sections.Add(new Section
                        {
                            Kind = kind, Name = name, When = when, Files = Texts(table, "files", where, required: true).Select(Resolve).ToList(), Where = match,
                            Since = since,
                        });
                        break;
                    default:
                        var show = Text(table, "show", where, "count");
                        if (show is not ("count" or "list"))
                            throw Bad(where, $"show should be count or list, not '{show}'");
                        sections.Add(new Section
                        {
                            Kind = kind, Name = name, When = when, File = Resolve(Text(table, "file", where)), Under = Text(table, "under", where),
                            Show = show, Text = table.TryGetValue("text", out _) ? Text(table, "text", where) : "",
                        });
                        break;
                }
            }
        }
        return new Config
        {
            Path = full,
            Root = raw.TryGetValue("root", out var r) ? r is string { Length: > 0 } root ? Resolve(root) : throw new UsageException($"{full}: root should be text.") : dir,
            Title = raw.TryGetValue("title", out var ti) ? ti as string ?? throw new UsageException($"{full}: title should be text.") : "Session start, {now}",
            Visible = !raw.TryGetValue("visible", out var vi) ? "headlines" : vi switch
            {
                true => "full",
                false => "none",
                "headlines" or "full" or "none" => (string)vi,
                _ => throw new UsageException($"{full}: visible should be headlines, full or none."),
            },
            Footer = raw.TryGetValue("footer", out var fo) ? fo as string ?? throw new UsageException($"{full}: footer should be text.") : "",
            SkipEnv = raw.TryGetValue("skip_env", out var se)
                ? se is TomlArray names && names.All(x => x is string) ? names.Cast<string>().ToList() : throw new UsageException($"{full}: skip_env should be a list of variable names.")
                : ["CLAUDE_HEADLESS"],
            Sections = sections,
            StateFile = raw.TryGetValue("state_file", out var sf)
                ? sf is string { Length: > 0 } state ? Resolve(state) : throw new UsageException($"{full}: state_file should be text.")
                : DefaultState(vars),
            AwayAfterWorkingDays = AwayDays(raw, full),
            Resolve = Resolve,
            Expand = Expand,
        };
    }

    /// <summary>%LOCALAPPDATA%/right-hand-session-start/state.json.</summary>
    public static string DefaultState(Func<string, string?> env)
    {
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "right-hand-session-start", "state.json"));
    }

    private static int AwayDays(TomlTable raw, string full)
    {
        if (!raw.TryGetValue("away", out var a))
            return 3;
        if (a is not TomlTable away)
            throw new UsageException($"{full}: away should be an [away] table.");
        foreach (var key in away.Keys)
            if (key != "after_working_days")
                throw new UsageException($"{full}: [away] has an unknown setting '{key}' (known: after_working_days).");
        return !away.TryGetValue("after_working_days", out var d) ? 3
            : d is long days && days is >= 1 and <= 366 ? (int)days
            : throw new UsageException($"{full}: [away] after_working_days should be a whole number of weekdays, 1 to 366.");
    }

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!System.IO.File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
