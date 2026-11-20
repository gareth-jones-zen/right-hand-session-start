using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RightHandSessionStart;

/// <summary>Whether this is the first session after time away (0.4.0), and since when. Since: the previous interactive
/// session's start (null if none is recorded); Weekdays: the weekdays between that day and today, neither counted.
/// On: at least [away] after_working_days of them, or the first such session today (so a /clear on the first morning
/// back keeps the away briefing).</summary>
public sealed record AwayMode(bool On, DateTime? Since, int Weekdays)
{
    public static readonly AwayMode None = new(false, null, 0);

    /// <summary>{since} in a command's arguments: the previous session's day (yyyy-MM-dd), or today if there's none.</summary>
    public string SinceText(DateTime now) => (Since ?? now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"First session since Tue 15 Dec: 13 weekdays".</summary>
    public string Line() => Since is { } since
        ? $"First session since {since.ToString("ddd d MMM", CultureInfo.InvariantCulture)}: {Weekdays} weekday{(Weekdays == 1 ? "" : "s")}"
        : "First session recorded";
}

/// <summary>The record of interactive sessions' starts, in state_file: the last one, and the away mode of today's first
/// session if it was one. Only the hook writes it (a headless run says nothing and records nothing); show reads it.</summary>
public static class Away
{
    /// <summary>Weekdays strictly between two days.</summary>
    public static int WeekdaysBetween(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var d = from.AddDays(1); d < to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        return count;
    }

    /// <summary>The away mode now, from the record. Pure apart from reading the file; a missing or unreadable record is
    /// no record.</summary>
    public static AwayMode Read(Config config, DateTime now)
    {
        var state = Load(config.StateFile);
        var today = DateOnly.FromDateTime(now);
        if (state["away"] is JsonObject away && Date(away["on"]) == today && Time(away["since"]) is { } awaySince)
            return new AwayMode(true, awaySince, (int?)away["weekdays"] ?? WeekdaysBetween(DateOnly.FromDateTime(awaySince), today));
        if (Time(state["last_start"]) is not { } last)
            return AwayMode.None;
        var weekdays = WeekdaysBetween(DateOnly.FromDateTime(last), today);
        return new AwayMode(weekdays >= config.AwayAfterWorkingDays, last, weekdays);
    }

    /// <summary>Records this session's start, and today's away mode if it is one. Never throws: a record that can't be
    /// written only means the next session can't tell.</summary>
    public static void Record(Config config, DateTime now, AwayMode mode)
    {
        try
        {
            var state = Load(config.StateFile);
            state["last_start"] = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            if (mode.On && mode.Since is { } since)
                state["away"] = new JsonObject
                {
                    ["on"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["since"] = since.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ["weekdays"] = mode.Weekdays,
                };
            else
                state.Remove("away");
            Directory.CreateDirectory(Path.GetDirectoryName(config.StateFile)!);
            var temp = config.StateFile + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                state.WriteTo(writer);
            File.Move(temp, config.StateFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject Load(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static DateTime? Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
        && DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static DateOnly? Date(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
        && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
