using System.Text.Json.Nodes;
using WinBackground;

namespace RightHandSessionStart.Tests;

/// <summary>right-hand-session-start on a temporary repo and settings, with a fake command runner.</summary>
public sealed class BriefingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rhss-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Completed> outputs = new()
    {
        ["queue"] = new(0, "## preps (1)\n   (prep) Say which are ready.\n  [ab12cd34] 2026-11-18  prep  Ben's 1:1\n", ""),
        ["mail"] = new(0, "3 emails and 0 invites to review; 1 deleted\n   `mail-triage list`\n", ""),
        ["watch"] = new(1, "1 problem(s)\n   STALE log x\n", ""),
        ["due"] = new(0, "Nothing due.\n", ""),
        ["broken"] = new(2, "", "settings: no such file"),
    };
    private readonly Dictionary<string, string?> env = new();

    private const string Settings = """
        root = "repo"

        [[section]]
        kind = "command"
        name = "pending"
        run = ["queue"]
        heading = "none"

        [[section]]
        kind = "command"
        name = "mail triage"
        run = ["mail"]

        [[section]]
        kind = "files"
        name = "summary drafts to review"
        files = ["repo/meetings/**/summaries/*.md"]
        where = { status = "draft" }

        [[section]]
        kind = "list"
        name = "review ledger"
        file = "repo/ledger.md"
        under = "Open"
        text = "open question(s)"

        [[section]]
        kind = "command"
        name = "watchdog"
        run = ["watch"]
        ok_exit = [0, 1]

        [[section]]
        kind = "command"
        name = "due"
        run = ["due"]
        heading = "each"
        drop_lines = ["Nothing due."]
        """;

    public BriefingTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "repo", "meetings", "a", "summaries"));
        Directory.CreateDirectory(Path.Combine(dir, "repo", "meetings", "b", "c", "summaries"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        // A draft whose status comes after a long topics line: a fixed-length read missed one like it.
        File.WriteAllText(Path.Combine(dir, "repo", "meetings", "a", "summaries", "2026-11-12-long.md"),
            $"---\ntitle: x\ntopics: [{string.Join(", ", Enumerable.Repeat("a-topic", 300))}]\nstatus: draft\n---\n# body\n");
        File.WriteAllText(Path.Combine(dir, "repo", "meetings", "b", "c", "summaries", "2026-11-16.md"), "---\nstatus: draft\n---\n");
        File.WriteAllText(Path.Combine(dir, "repo", "meetings", "b", "c", "summaries", "2026-11-17.md"), "---\nstatus: final\n---\nstatus: draft\n");
        File.WriteAllText(Path.Combine(dir, "repo", "ledger.md"), "# Ledger\n\n## Open\n\n- one?\n- two?\n\n## Settled\n\n- old\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private (int Code, string Out, string Err) Run(string stdin, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args], new CliHost
        {
            Out = output, Err = errors, In = new StringReader(stdin), Env = name => env.GetValueOrDefault(name),
            Now = () => new DateTime(2026, 11, 18, 19, 38, 0),
            Run = (argv, cwd, _) =>
            {
                Assert.Equal(dir, cwd);   // commands run from the settings folder
                return outputs[Path.GetFileName(argv[0])];
            },
        });
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void The_briefing_shows_each_section_that_has_something_to_say()
    {
        var (code, output, _) = Run("", "show");
        Assert.Equal(0, code);
        Assert.Equal(
            "Session start, Wed 18 Nov 19:38\n\n" +
            "## preps (1)\n   (prep) Say which are ready.\n  [ab12cd34] 2026-11-18  prep  Ben's 1:1\n\n" +
            "## mail triage: 3 emails and 0 invites to review; 1 deleted\n   `mail-triage list`\n\n" +
            "## summary drafts to review: 2, oldest 2026-11-12\n   meetings/a/summaries/2026-11-12-long.md\n   meetings/b/c/summaries/2026-11-16.md\n\n" +
            "## review ledger: 2 open question(s)\n\n" +
            "## watchdog: 1 problem(s)\n   STALE log x\n", output);   // nothing due: left out
    }

    [Fact]
    public void A_failing_section_says_so_and_the_rest_still_show()
    {
        outputs["mail"] = outputs["broken"];
        File.WriteAllText(Path.Combine(dir, "repo", "ledger.md"), "# Ledger\n\n## Questions\n\n- one?\n\n## Settled\n\n- old\n");
        var output = Run("", "show").Out;
        Assert.Contains("## mail triage: couldn't read it (mail exited 2: settings: no such file)\n", output);
        Assert.Contains("## review ledger: couldn't read it (InvalidDataException: ledger.md has no '## Open' heading)", output);   // a renamed heading isn't read as the whole file
        Assert.Contains("## watchdog: 1 problem(s)", output);
        outputs["due"] = new(0, "Thu 19 Nov  waiting 2: x\nFri 20 Nov  mine 1: y\n", "");
        Assert.Contains("## due: Thu 19 Nov  waiting 2: x\n## due: Fri 20 Nov  mine 1: y\n", Run("", "show").Out);
    }

    [Fact]
    public void The_hook_gives_claude_the_briefing_but_says_nothing_in_a_headless_run()
    {
        var (code, output, _) = Run("""{"session_id": "x", "hook_event_name": "SessionStart", "source": "startup"}""", "hook");
        Assert.Equal(0, code);
        var json = JsonNode.Parse(output)!["hookSpecificOutput"]!;
        Assert.Equal("SessionStart", (string?)json["hookEventName"]);
        Assert.StartsWith("Session start, Wed 18 Nov 19:38\n\n## preps (1)", (string?)json["additionalContext"]);

        env["CLAUDE_HEADLESS"] = "1";
        Assert.Equal((0, ""), (Run("{}", "hook").Code, Run("{}", "hook").Out));
        env.Remove("CLAUDE_HEADLESS");

        File.WriteAllText(Path.Combine(dir, "config.toml"), "colour = \"red\"\n");   // never blocks a session
        var broken = Run("{}", "hook");
        Assert.Equal(0, broken.Code);
        Assert.Contains("couldn't build the briefing", (string?)JsonNode.Parse(broken.Out)!["hookSpecificOutput"]!["additionalContext"]);
    }

    [Fact]
    public void Json_gives_each_section_and_its_lines()
    {
        var json = JsonNode.Parse(Run("", "show", "--json").Out)!;
        Assert.Equal("2026-11-18T19:38", (string?)json["at"]);
        var sections = json["sections"]!.AsArray();
        Assert.Equal(6, sections.Count);
        Assert.Equal(["2, oldest 2026-11-12", "   meetings/a/summaries/2026-11-12-long.md", "   meetings/b/c/summaries/2026-11-16.md"],
            sections[2]!["lines"]!.AsArray().Select(n => (string?)n));
        Assert.Empty(sections[5]!["lines"]!.AsArray());
    }

    [Theory]
    [InlineData("[[section]]\nkind = \"shell\"\nname = \"x\"", "kind should be command, files or list")]
    [InlineData("[[section]]\nkind = \"command\"\nname = \"x\"\nrun = [\"a\"]\nheading = \"top\"", "heading should be inline, above, each or none")]
    [InlineData("[[section]]\nkind = \"list\"\nname = \"x\"\nfile = \"a\"\nunder = \"b\"\nrun = [\"c\"]", "unknown setting 'run'")]
    [InlineData("colour = \"red\"", "unknown setting 'colour'")]
    public void Settings_are_read_strictly(string text, string message)
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), text + "\n");
        var (code, _, err) = Run("", "show");
        Assert.Equal(2, code);
        Assert.Contains(message, err);
    }
}
