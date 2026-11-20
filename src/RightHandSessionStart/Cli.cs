using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RightHandSessionStart;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public TextReader In { get; init; } = Console.In;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public CommandRunner Run { get; init; } = Briefing.RunCommand;
}

/// <summary>right-hand-session-start: the briefing at the start of an assistant session. Exit codes: 0 done; 1
/// failed; 2 settings or arguments. A section that fails is shown as failed; it doesn't fail the briefing.</summary>
public static class Cli
{
    public const string Prog = "right-hand-session-start";

    private const string Usage = "usage: right-hand-session-start [-h] [--version] [--config CONFIG] {show,hook,config} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "right-hand-session-start: the briefing at the start of an assistant session: what the background tools have left\n" +
        "for you, each section from the settings (a command's output, files with a frontmatter field, bullets under a\n" +
        "heading). Part of right-hand.\n\n" +
        "    right-hand-session-start show        the briefing\n" +
        "        --json                           as JSON, a section at a time\n" +
        "        --away                           as the first session after time away would show it\n" +
        "    right-hand-session-start hook        for a Claude Code SessionStart hook: reads the hook's JSON, and gives\n" +
        "                                         Claude the briefing (nothing in a headless run: skip_env)\n" +
        "    right-hand-session-start config      create the settings file (if missing) and say where it is\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\right-hand-session-start\\config.toml)\n";

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        string? configPath = null;
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return 0;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return 0;
            }
            if (arg == "--config" && i + 1 < argv.Count)
                configPath = argv[++i];
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                configPath = arg["--config=".Length..];
            else
                return Bad(host, $"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            return Bad(host, "the following arguments are required: command");
        var command = argv[i++];
        var rest = argv.Skip(i).ToList();
        if (rest.Contains("-h") || rest.Contains("--help"))
        {
            host.Out.Write(Help);
            return 0;
        }
        var json = rest.Remove("--json");
        var forceAway = command == "show" && rest.Remove("--away");
        if (command is not ("show" or "hook" or "config"))
            return Bad(host, $"argument command: invalid choice: '{command}' (choose from 'show', 'hook', 'config')");
        if (rest.Count > 0 || (json && command != "show"))
            return Bad(host, $"unrecognized arguments: {string.Join(" ", json && command != "show" ? ["--json", .. rest] : rest)}");

        try
        {
            if (command == "config")
            {
                host.Out.WriteLine(RightHandSessionStart.Config.CreateDefault(configPath ?? host.Env(RightHandSessionStart.Config.EnvConfig), host.Env));
                return 0;
            }
            if (command == "hook")
                return Hook(configPath, host);
            var config = RightHandSessionStart.Config.Load(configPath, host.Env);
            var now = host.Now();
            var away = Away.Read(config, now);   // show only reads the record; the hook writes it
            if (forceAway)
                away = away with { On = true };
            var parts = Briefing.Gather(config, host.Run, away, now);
            host.Out.Write(json ? Json(parts, now, away) : Briefing.Text(config, parts, now, away));
            return 0;
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int Bad(CliHost host, string message)
    {
        host.Err.WriteLine(Usage);
        host.Err.WriteLine($"{Prog}: error: {message}");
        return 2;
    }

    /// <summary>A Claude Code SessionStart hook: the briefing as additionalContext, so it's in front of Claude when the
    /// session opens, and (visible, the default) as a systemMessage, so the person sees it too. In a tool's headless run (a skip_env variable is set: CLAUDE_HEADLESS, from background-tools-lib's
    /// ClaudeHeadless) it says nothing. It never blocks a session: settings it can't read, or anything else that goes
    /// wrong, become a line in the context saying so, and it exits 0.</summary>
    private static int Hook(string? configPath, CliHost host)
    {
        try
        {
            host.In.ReadToEnd();   // the hook's JSON (session id, source...): not needed, but read so the writer isn't blocked
        }
        catch (IOException)
        {
        }
        string text;
        var visible = "headlines";
        string? headlines = null;
        try
        {
            var config = RightHandSessionStart.Config.Load(configPath, host.Env);
            if (config.SkipEnv.Any(name => host.Env(name) is { Length: > 0 }))
                return 0;
            visible = config.Visible;
            var now = host.Now();
            var away = Away.Read(config, now);
            Away.Record(config, now, away);   // this session's start, before anything slow can fail
            var parts = Briefing.Gather(config, host.Run, away, now);
            text = Briefing.Text(config, parts, now, away);
            headlines = Briefing.Headlines(config, parts, now, away);
        }
        catch (Exception error)
        {
            if (host.Env("CLAUDE_HEADLESS") is { Length: > 0 })
                return 0;
            text = $"right-hand-session-start couldn't build the briefing: {error.Message}\n";
        }
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "SessionStart", ["additionalContext"] = text },
        };
        if (visible != "none")   // shown to the person, on screen: the headlines (0.3.0) or the whole briefing (0.2.0)
            output["systemMessage"] = (visible == "full" || headlines is null ? text : headlines).TrimEnd('\n');
        host.Out.Write(Write(output) + "\n");
        return 0;
    }

    internal static string Json(IReadOnlyList<Part> parts, DateTime now, AwayMode? away = null)
    {
        var sections = new JsonArray();
        foreach (var part in parts)
        {
            var lines = new JsonArray();
            foreach (var line in part.Lines)
                lines.Add((JsonNode)JsonValue.Create(line));
            sections.Add((JsonNode)new JsonObject
            {
                ["name"] = part.Section.Name, ["kind"] = part.Section.Kind, ["lines"] = lines, ["error"] = part.Error,
            });
        }
        return Write(new JsonObject
        {
            ["at"] = now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), ["sections"] = sections,
            ["away"] = away?.On == true
                ? new JsonObject
                {
                    ["since"] = away.Since?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), ["weekdays"] = away.Weekdays, ["line"] = away.Line(),
                }
                : null,
        }) + "\n";
    }

    private static string Write(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
