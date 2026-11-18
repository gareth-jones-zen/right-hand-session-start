using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinBackground;

namespace RightHandSessionStart;

/// <summary>What one section came to: the lines to show (empty: nothing to say, so it's left out), or an error.</summary>
public sealed record Part(Section Section, IReadOnlyList<string> Lines, string? Error = null)
{
    public bool Empty => Error is null && Lines.Count == 0;
}

/// <summary>Runs a command for a section: the program, its arguments, the folder to run in, a timeout.</summary>
public delegate Completed CommandRunner(IReadOnlyList<string> argv, string cwd, TimeSpan timeout);

public static partial class Briefing
{
    public static Completed RunCommand(IReadOnlyList<string> argv, string cwd, TimeSpan timeout) =>
        Shell.Run(argv[0], argv.Skip(1), timeout, workingDirectory: cwd, utf8: true);

    public static List<Part> Gather(Config config, CommandRunner run) => config.Sections.Select(s =>
    {
        try
        {
            return s.Kind switch
            {
                "command" => Command(config, s, run),
                "files" => Files(config, s),
                _ => List(s),
            };
        }
        catch (Exception error)
        {
            return new Part(s, [], $"{error.GetType().Name}: {error.Message}");
        }
    }).ToList();

    /// <summary>The briefing as text: the title, then each section that has something to say.</summary>
    public static string Text(Config config, IReadOnlyList<Part> parts, DateTime now)
    {
        var text = new StringBuilder();
        text.Append(config.Title.Replace("{now}", now.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)).Append("\n\n");
        foreach (var part in parts.Where(p => !p.Empty))
        {
            if (part.Error is { } error)
            {
                text.Append($"## {part.Section.Name}: couldn't read it ({error})\n\n");
                continue;
            }
            var heading = part.Section.Kind == "command" ? part.Section.Heading : "inline";
            var lines = part.Lines;
            switch (heading)
            {
                case "none":
                    foreach (var line in lines)
                        text.Append(line).Append('\n');
                    break;
                case "each":
                    foreach (var line in lines)
                        text.Append($"## {part.Section.Name}: {line.Trim()}\n");
                    break;
                case "above":
                    text.Append($"## {part.Section.Name}\n");
                    foreach (var line in lines)
                        text.Append(line).Append('\n');
                    break;
                default:
                    text.Append($"## {part.Section.Name}: {lines[0].Trim()}\n");
                    foreach (var line in lines.Skip(1))
                        text.Append(line).Append('\n');
                    break;
            }
            text.Append('\n');
        }
        return text.ToString().TrimEnd('\n') + "\n";
    }

    // ---- command --------------------------------------------------------------------------------------------------

    internal static Part Command(Config config, Section section, CommandRunner run)
    {
        var done = run(section.Run, Path.GetDirectoryName(config.Path)!, TimeSpan.FromSeconds(section.TimeoutSeconds));
        if (!section.OkExit.Contains(done.ExitCode))
        {
            var said = (done.StandardError.Trim().Length > 0 ? done.StandardError : done.StandardOutput).Trim();
            return new Part(section, [], $"{Path.GetFileName(section.Run[0])} exited {done.ExitCode}" + (said.Length > 0 ? $": {Head(said, 300)}" : ""));
        }
        var lines = done.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.Trim().Length > 0 && !section.DropLines.Contains(l.Trim(), StringComparer.Ordinal))
            .Select(l => l.TrimEnd()).ToList();
        return new Part(section, lines);
    }

    private static string Head(string text, int length) => text.Length <= length ? text : text[..length] + "...";

    // ---- files ----------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    /// <summary>Files whose frontmatter has every field in Where: "5, oldest 2026-11-12", then each file. Only the
    /// frontmatter is read (a file without it never matches a where), so a long one isn't cut short.</summary>
    internal static Part Files(Config config, Section section)
    {
        var found = new List<(string Path, string Date)>();
        foreach (var file in section.Files.SelectMany(Glob).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (section.Where.Count > 0 && !Matches(Frontmatter(file), section.Where))
                continue;
            var date = Date().Match(Path.GetFileName(file));
            found.Add((Shown(config, file), date.Success ? date.Value : ""));
        }
        if (found.Count == 0)
            return new Part(section, []);
        found = found.OrderBy(f => f.Date, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
        var oldest = found.FirstOrDefault(f => f.Date.Length > 0).Date;
        return new Part(section, [$"{found.Count}" + (oldest is { Length: > 0 } ? $", oldest {oldest}" : ""), .. found.Select(f => "   " + f.Path)]);
    }

    internal static string Shown(Config config, string path) =>
        (path.StartsWith(config.Root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(config.Root, path) : path).Replace('\\', '/');

    /// <summary>A markdown file's frontmatter (between a first line of --- and the next ---), or "" if it has none.</summary>
    internal static string Frontmatter(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            if (reader.ReadLine()?.TrimEnd() != "---")
                return "";
            var text = new StringBuilder();
            for (var line = reader.ReadLine(); line is not null && line.TrimEnd() != "---"; line = reader.ReadLine())
                text.Append(line).Append('\n');
            return text.ToString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    internal static bool Matches(string frontmatter, IReadOnlyDictionary<string, string> where) =>
        where.All(w => Regex.IsMatch(frontmatter, $@"(?m)^{Regex.Escape(w.Key)}:\s*[""']?{Regex.Escape(w.Value)}[""']?\s*$"));

    /// <summary>Files matching a pattern: * and ? within one folder level, ** for any number of folders; matching
    /// ignores case, and a wildcard skips names starting with ".".</summary>
    public static IEnumerable<string> Glob(string pattern)
    {
        var root = Path.GetPathRoot(pattern) ?? "";
        var parts = pattern[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> found = [root.Length == 0 ? "." : root];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var last = i == parts.Length - 1;
            if (part == "**")
            {
                found = found.SelectMany(f => Directory.Exists(f) ? new[] { f }.Concat(SafeDirs(f, recursive: true)) : []).ToList();
                continue;
            }
            if (part.IndexOfAny(['*', '?']) < 0)
            {
                found = found.Select(f => Path.Join(f, part)).Where(p => last ? File.Exists(p) : Directory.Exists(p)).ToList();
                continue;
            }
            var match = new Regex("^" + Regex.Escape(part).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var hidden = part.StartsWith('.');
            found = found.SelectMany(folder =>
            {
                if (!Directory.Exists(folder))
                    return [];
                try
                {
                    var entries = last ? Directory.EnumerateFiles(folder) : Directory.EnumerateDirectories(folder);
                    return entries.Where(e => Path.GetFileName(e) is var n && (hidden || !n.StartsWith('.')) && match.IsMatch(n)).ToList();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            }).ToList();
        }
        return found;
    }

    private static IEnumerable<string> SafeDirs(string folder, bool recursive)
    {
        try
        {
            return Directory.EnumerateDirectories(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ---- list -----------------------------------------------------------------------------------------------------

    /// <summary>The "- " bullets under a "## " heading in a markdown file (to the next "## "): their count with the
    /// section's text ("3 open question(s) for GJ"), or each one. None: nothing to say. A missing heading is an error,
    /// so a renamed one isn't read as the whole file.</summary>
    internal static Part List(Section section)
    {
        var lines = File.ReadAllText(section.File!, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal) && l[3..].Trim().Equals(section.Under, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            throw new InvalidDataException($"{Path.GetFileName(section.File)} has no '## {section.Under}' heading");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        var bullets = lines[(start + 1)..(end < 0 ? lines.Length : end)].Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).ToList();
        if (bullets.Count == 0)
            return new Part(section, []);
        var head = $"{bullets.Count}" + (section.Text.Length > 0 ? $" {section.Text}" : "");
        return new Part(section, section.Show == "list" ? [head, .. bullets.Select(b => "   - " + b)] : [head]);
    }
}
