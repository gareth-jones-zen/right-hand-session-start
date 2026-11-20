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
    private DateTime now = new(2026, 11, 18, 19, 38, 0);
    private readonly List<IReadOnlyList<string>> ran = [];

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
        env["LOCALAPPDATA"] = Path.Combine(dir, "local");   // the default state file goes here, not the real one
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
            Now = () => now,
            Run = (argv, cwd, _) =>
            {
                ran.Add(argv);
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
        Assert.Equal(   // on screen: a line per section (0.3.0)
            "Session start, Wed 18 Nov 19:38\n" +
            "- Pending: preps (1)\n" +
            "- Mail triage: 3 emails and 0 invites to review; 1 deleted\n" +
            "- Summary drafts to review: 2, oldest 2026-11-12\n" +
            "- Review ledger: 2 open question(s)\n" +
            "- Watchdog: 1 problem(s)", (string?)JsonNode.Parse(output)!["systemMessage"]);

        File.WriteAllText(Path.Combine(dir, "config.toml"), "visible = \"full\"\nfooter = \"x\"\n" + Settings);
        Assert.StartsWith("Session start, Wed 18 Nov 19:38\n\n## preps (1)", (string?)JsonNode.Parse(Run("{}", "hook").Out)!["systemMessage"]);
        File.WriteAllText(Path.Combine(dir, "config.toml"), "visible = false\n" + Settings);
        Assert.Null(JsonNode.Parse(Run("{}", "hook").Out)!["systemMessage"]);
        File.WriteAllText(Path.Combine(dir, "config.toml"), "footer = \"Say anything for the rest.\"\n" + Settings);
        Assert.EndsWith("- Watchdog: 1 problem(s)\nSay anything for the rest.", (string?)JsonNode.Parse(Run("{}", "hook").Out)!["systemMessage"]);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);

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
    [InlineData("short line", "short line")]
    [InlineData("a   b    c", "a b c")]
    public void Headlines_squeeze_lines(string line, string expected) => Assert.Equal(expected, Briefing.Short(line));

    [Fact]
    public void A_long_headline_is_cut_before_its_bracket()
    {
        var mail = "28 emails and 1 invite to review; 24 deleted by the rules since the last clear (15 Cancelled meetings, 2 Salesforce technical notices)";
        Assert.Equal("28 emails and 1 invite to review; 24 deleted by the rules since the last clear", Briefing.Short(mail));
        Assert.Equal(new string('x', 107) + "...", Briefing.Short(new string('x', 200)));
    }

    private const string AwaySettings = """

        [away]
        after_working_days = 3

        [[section]]
        kind = "files"
        name = "return pack"
        when = "away"
        since = true
        files = ["repo/reviews/re-entry/????-??-??.md"]

        [[section]]
        kind = "command"
        name = "deleted since"
        when = "away"
        run = ["mail", "deleted", "--since", "{since}"]
        """;

    private string? Context(string output) => (string?)JsonNode.Parse(output)!["hookSpecificOutput"]!["additionalContext"];

    private string? Screen(string output) => (string?)JsonNode.Parse(output)!["systemMessage"];

    [Fact]
    public void After_three_weekdays_away_the_first_session_says_so_and_shows_the_away_sections()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + AwaySettings);
        Directory.CreateDirectory(Path.Combine(dir, "repo", "reviews", "re-entry"));
        File.WriteAllText(Path.Combine(dir, "repo", "reviews", "re-entry", "2026-11-09.md"), "# an older pack");
        File.WriteAllText(Path.Combine(dir, "repo", "reviews", "re-entry", "2027-01-04.md"), "# [DRAFT] Return pack");
        File.WriteAllText(Path.Combine(dir, "repo", "reviews", "re-entry", "2026-12-16-before-you-go.md"), "# [DRAFT] Before you go");

        now = new DateTime(2026, 12, 15, 17, 0, 0);   // the last session before the leave
        var first = Run("{}", "hook").Out;
        Assert.DoesNotContain("First session since", Context(first));
        Assert.DoesNotContain("return pack", Context(first));
        Assert.True(File.Exists(Path.Combine(dir, "local", "right-hand-session-start", "state.json")));

        now = new DateTime(2027, 1, 4, 8, 5, 0);   // back: 16 Dec to 1 Jan is 13 weekdays
        ran.Clear();
        var back = Run("{}", "hook").Out;
        Assert.StartsWith("Session start, Mon 04 Jan 08:05\n\nFirst session since Tue 15 Dec: 13 weekdays\n\n## preps (1)", Context(back));
        Assert.Contains("## return pack: 1, oldest 2027-01-04\n   reviews/re-entry/2027-01-04.md\n", Context(back));   // since the last session; not the before-you-go
        Assert.Contains(["mail", "deleted", "--since", "2026-12-15"], ran);
        Assert.StartsWith("Session start, Mon 04 Jan 08:05\nFirst session since Tue 15 Dec: 13 weekdays\n- Pending: preps (1)", Screen(back));
        Assert.Contains("- Return pack: 1, oldest 2027-01-04", Screen(back));

        now = new DateTime(2027, 1, 4, 11, 0, 0);   // a /clear later that morning: still the first day back
        Assert.Contains("First session since Tue 15 Dec: 13 weekdays", Context(Run("{}", "hook").Out));
        now = new DateTime(2027, 1, 5, 9, 0, 0);    // the next day, ordinary again
        var next = Context(Run("{}", "hook").Out);
        Assert.DoesNotContain("First session since", next);
        Assert.DoesNotContain("return pack", next);
    }

    [Fact]
    public void A_weekend_or_two_days_is_not_away_and_show_only_reads()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + AwaySettings);
        now = new DateTime(2026, 11, 13, 17, 0, 0);   // Friday
        Run("{}", "hook");
        now = new DateTime(2026, 11, 16, 9, 0, 0);    // Monday: no weekdays missed
        Assert.DoesNotContain("First session since", Context(Run("{}", "hook").Out));
        now = new DateTime(2026, 11, 19, 9, 0, 0);    // Thursday: Tue and Wed missed
        Assert.DoesNotContain("First session since", Run("", "show").Out);
        now = new DateTime(2026, 11, 20, 9, 0, 0);    // Friday: three missed, and show didn't record Thursday
        Assert.Contains("First session since Mon 16 Nov: 3 weekdays", Run("", "show").Out);
        Assert.Contains("First session since Mon 16 Nov: 3 weekdays", Run("", "show").Out);   // still: show records nothing
        var json = JsonNode.Parse(Run("", "show", "--json").Out)!;
        Assert.Equal(3, (int?)json["away"]!["weekdays"]);
        Assert.Equal("2026-11-16T09:00", (string?)json["away"]!["since"]);
        // --away shows it as an away session whatever the gap; a headless run neither shows nor records anything.
        now = new DateTime(2026, 11, 16, 10, 0, 0);
        Assert.Contains("First session since Mon 16 Nov: 0 weekdays", Run("", "show", "--away").Out);
        env["CLAUDE_HEADLESS"] = "1";
        now = new DateTime(2026, 12, 1, 10, 0, 0);
        Assert.Equal("", Run("{}", "hook").Out);
        env.Remove("CLAUDE_HEADLESS");
        Assert.Contains("First session since Mon 16 Nov: 10 weekdays", Context(Run("{}", "hook").Out));
        Assert.Equal(2, Run("", "hook", "--away").Code);
    }

    [Fact]
    public void Without_a_record_or_with_a_higher_threshold_it_is_an_ordinary_session()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + AwaySettings.Replace("after_working_days = 3", "after_working_days = 5"));
        Assert.DoesNotContain("First session", Context(Run("{}", "hook").Out));
        now = now.AddDays(6);   // Wed 18 Nov to Tue 24 Nov: Thu, Fri, Mon missed
        Assert.DoesNotContain("First session", Context(Run("{}", "hook").Out));
        File.WriteAllText(Path.Combine(dir, "config.toml"), "state_file = \"elsewhere/state.json\"\n" + Settings);
        Run("{}", "hook");
        Assert.True(File.Exists(Path.Combine(dir, "elsewhere", "state.json")));
        Assert.Equal([3, 1, 0], new[] { Away.WeekdaysBetween(new(2026, 11, 16), new(2026, 11, 20)), Away.WeekdaysBetween(new(2026, 11, 13), new(2026, 11, 17)),
            Away.WeekdaysBetween(new(2026, 11, 13), new(2026, 11, 16)) });
    }

    [Theory]
    [InlineData("[[section]]\nkind = \"shell\"\nname = \"x\"", "kind should be command, files or list")]
    [InlineData("[[section]]\nkind = \"list\"\nname = \"x\"\nfile = \"a\"\nunder = \"b\"\nwhen = \"sometimes\"", "when should be always or away")]
    [InlineData("[[section]]\nkind = \"command\"\nname = \"x\"\nrun = [\"a\"]\nsince = true", "unknown setting 'since'")]
    [InlineData("[away]\nafter_working_days = 0", "after_working_days should be a whole number")]
    [InlineData("[away]\ndays = 3", "[away] has an unknown setting 'days'")]
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
