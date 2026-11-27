# right-hand-session-start

The briefing at the start of an assistant session: everything the background tools have left for you, in one place,
in front of the assistant before you've typed a word. Part of **right-hand**, a kit for running Claude Code as an
executive assistant; on its own it's a small, settings-driven briefing that any Claude Code project can use.

```
Session start, Wed 18 Nov 19:38

## preps (2)
   (prep) Say which preps are ready; once read, remove its [DRAFT] marker, then done.
  [51ff4d47] 2026-11-18  prep             Weekly 1-2-1 - Dan, 2026-11-18 11:00

## mail triage: 28 emails and 1 invite to review; 24 deleted by the rules since the last clear (...)

## summary drafts to review: 5, oldest 2026-11-12
   meetings/ad-hoc/summaries/2026-11-12-AI_Explore.md

## review ledger: 10 open question(s)

## watchdog: all 30 checks fine (outputs, watchers' logs, tasks)
```

Windows (x64). Built in .NET 11 with Native AOT: no runtime to install.

## How it works

**Each section is in the settings, and each tool prints its own briefing line.** The briefing doesn't read any tool's
data: a section runs the tool's own summary command (`review-queue list`, `mail-triage summary`,
`tool-watchdog check --brief`, `md-inbox due`) and shows what it prints. So a tool can change how it keeps its data
without the briefing noticing, or going quietly wrong. There are three kinds of section:

| Kind | What it shows |
|---|---|
| `command` | What a program prints, under a heading. Lines that mean "nothing to say" can be dropped; a section with nothing left is left out |
| `files` | Files matching a pattern whose frontmatter has a given field (`status: draft`, say): how many, the oldest date in their names, and each file |
| `list` | The bullets under a `## ` heading in one markdown file: how many, or each one |

A section that fails (a program missing, an exit code it didn't expect, a heading that isn't there) says so in its
place; the rest still show.

**The sections run at the same time (0.5.0)** and are shown in the settings' order, so the briefing takes as long as
its slowest section, not the sum of them all (an unsigned program can be held for several seconds while Microsoft
Defender asks the cloud about it, and with each tool started one after another those holds added up). Each keeps its
own `timeout_seconds`. Give each section its own tool, as two sections running the same tool at once could meet in
its files.

## Install

1. Download `right-hand-session-start-<version>-win-x64.zip` from
   [Releases](https://github.com/gareth-jones-zen/right-hand-session-start/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\right-hand-session-start`.
3. Write your sections into a settings file (`right-hand-session-start config` makes one), and try
   `right-hand-session-start --config <file> show`.
4. Add the hook to your project's `.claude/settings.json`:

```json
{
  "hooks": {
    "SessionStart": [{
      "matcher": "startup|resume|clear",
      "hooks": [{
        "type": "command",
        "command": "C:\\Users\\you\\AppData\\Local\\Programs\\right-hand-session-start\\right-hand-session-start-background.exe",
        "args": ["--config", "C:\\path\\to\\your\\project\\config\\right-hand-session-start.toml", "hook"],
        "timeout": 120
      }]
    }]
  }
}
```

The program and its settings are given as full paths, with the arguments listed separately (no shell in between,
so nothing to quote and no window).

It holds two programs: `right-hand-session-start.exe`, for a console, and `right-hand-session-start-background.exe`,
the same program with no window, which the hook runs.

## The hook

`hook` reads Claude Code's SessionStart JSON on standard input and answers with the briefing as `additionalContext`,
so it's in the assistant's context when the session opens.

- **Headless runs are left alone.** When one of `skip_env`'s variables is set (by default `CLAUDE_HEADLESS`, which
  background-tools-lib's ClaudeHeadless and the Python claude-headless set for every run they start), it says
  nothing: a tool's job drafting a summary in the same project has no use for your briefing.
- **You see the headlines** (`visible = "headlines"`, the default, 0.3.0): a line per section comes up on screen as
  the session opens (a `systemMessage`), with `footer` under them if you set one; Claude gets the whole briefing. The
  detail is written for an assistant (ids, guidance), so ask it for the briefing, or have your project's CLAUDE.md
  say to open the first reply with it. `visible = "full"` shows you the whole briefing; `"none"`, nothing.
- **It never blocks a session.** Settings it can't read, or anything else that goes wrong, become one line in the
  context saying so, and it exits 0.
- **It logs how long it took (0.5.0)**: a line per interactive session in `log_file`, with the whole run and each
  section in milliseconds (`hook 1840 ms: pending 120, mail triage 95, ...`), so a slow section can be found. Never
  in the briefing. `show` writes nothing, so it doesn't log; `show --json` gives each section's `ms` instead.

## Away mode (0.4.0)

**The first session after a few days away is a different session:** a week or three of the tools working without you
leaves more than the ordinary briefing can say well, and the first thing to know is what kept running, not every item.
So the hook records when each interactive session starts (`state_file`; a headless run, as above, records nothing),
and when the gap since the last one is at least `[away] after_working_days` weekdays (3 by default; the days between,
neither counted, so a weekend alone never is), the session is in **away mode**:

- the briefing's first line says so: `First session since Tue 15 Dec: 13 weekdays` (on screen too);
- **sections marked `when = "away"` are shown only then**: a pointer to a return pack, say;
- **`{since}` in a command's arguments is the last session's day** (`yyyy-mm-dd`; today if none is recorded), so a tool
  can summarise since then rather than since its own last clear. It's filled in every session, away or not;
- **a `files` section with `since = true`** lists only files whose name's date is on or after that day.

**The rest of that day stays in away mode** (a `/clear` on the first morning back gives the same briefing); the next day
is ordinary. Only weekdays count, as the tool knows nothing of your calendar: a bank holiday is a weekday. `show` reads
the record and never writes it, so trying it changes nothing; `show --away` shows a briefing as away mode would.

## Commands

```
right-hand-session-start show        the briefing (--json: a section at a time, with its lines, any error and its time
                                     in ms, and the away mode; --away: as the first session after time away would show it)
right-hand-session-start hook        for a Claude Code SessionStart hook (above)
right-hand-session-start config      create the settings file (if missing) and say where it is
```

**Exit codes:** 0 done; 1 failed; 2 settings or arguments.

## Settings

`%APPDATA%\right-hand-session-start\config.toml` by default (`--config` or `RIGHT_HAND_SESSION_START_CONFIG`).

```toml
base = "base-config.toml"          # optional: settings shared with other tools ([paths] as %VARIABLES%)
root = ".."                        # file paths are shown relative to it (default: this folder)
title = "Session start, {now}"     # {now}: "Wed 18 Nov 19:38"
skip_env = ["CLAUDE_HEADLESS"]     # hook: say nothing when one of these is set
visible = "headlines"              # hook: what you see on screen: headlines, full or none (Claude gets it all)
footer = "Say anything for the full briefing."   # hook: a line under the headlines
state_file = "%LOCALAPPDATA%/right-hand-session-start/state.json"   # each session's start (this is the default)
log_file = "%LOCALAPPDATA%/right-hand-session-start/right-hand-session-start.log"   # hook: a line per session with each
                                   # section's time (the default: right-hand-session-start.log beside state_file)

[away]
after_working_days = 3             # the first session after at least this many weekdays without one is in away mode

[[section]]
kind = "command"
name = "mail triage"
run = ["%LOCALAPPDATA%/Programs/mail-triage/mail-triage.exe", "--config", "mail-triage.toml", "summary"]
heading = "inline"                 # inline: "## name: first line", then the rest; above: "## name", then every line;
                                   # each: "## name: line" for every line; none: the lines as they are
ok_exit = [0]                      # exit codes that mean it worked (tool-watchdog's 1 means "something is stale")
drop_lines = ["Nothing due."]      # lines that mean there's nothing to say
timeout_seconds = 120

[[section]]
kind = "files"
name = "summary drafts to review"
files = ["../meetings/**/summaries/*.md"]   # * and ? within a folder, ** for any number of folders
where = { status = "draft" }       # frontmatter fields it must have (only the frontmatter is read, all of it)

[[section]]
kind = "files"
name = "return pack"
when = "away"                      # any section: shown only in away mode (default "always")
files = ["../reviews/re-entry/????-??-??.md"]
since = true                       # files: only those dated (by name) on or after the last session's day

[[section]]
kind = "list"
name = "review ledger"
file = "../meetings/review-ledger.md"
under = "Open"                     # the "## " heading the bullets are under (to the next "## ")
text = "open question(s)"          # after the count
show = "count"                     # or list: each bullet too
```

Commands run from the settings file's folder. An argument with a slash is a path, relative to that folder; anything
else (a program on the PATH, an option) only has its variables expanded. The file is read strictly: an unknown
setting, or one of the wrong type, is an error that names it.

## Building

You need the .NET 11 SDK (`global.json` pins the feature band). Native AOT publishing also needs the Visual Studio C++
build tools. The shared libraries are a sibling checkout of
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib):

```
dotnet test -c Release
dotnet publish src/RightHandSessionStart.Console -c Release -r win-x64 -o publish
dotnet publish src/RightHandSessionStart.Background -c Release -r win-x64 -o publish
```

A tag `v*` builds, tests and publishes a release on GitHub Actions, with SHA-256 checksums and a provenance attestation.
