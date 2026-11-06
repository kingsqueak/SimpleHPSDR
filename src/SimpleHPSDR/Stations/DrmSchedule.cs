using System.Text.Json;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.Stations;

/// <summary>The DRM broadcast list: EiBi and DRMDX merged by the decoder library.</summary>
public static class DrmSchedule
{
    private const int Daily = 0b111_1111;

    public sealed record Source(string Title, string Url, string File);

    public static List<Source> Sources(string dir)
    {
        using var doc = JsonDocument.Parse(DrmNative.ScheduleSources(dir));
        return doc.RootElement.EnumerateArray()
            .Select(e => new Source(e.GetProperty("title").GetString()!, e.GetProperty("url").GetString()!,
                e.GetProperty("file").GetString()!))
            .ToList();
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>The list says only whether a line runs today, which is all this needs:
    /// it's read again each time the listing is refreshed.</summary>
    public static List<Broadcast> Load(string dir)
    {
        using var doc = JsonDocument.Parse(DrmNative.Schedule(dir));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var list = new List<Broadcast>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            bool runsToday = e.GetProperty("today").GetBoolean();
            var days = new DayRule(Daily, Text(e, "days"), runsToday ? null : d => d != today);
            list.Add(new Broadcast(e.GetProperty("khz").GetDouble(), e.GetProperty("start").GetInt32(),
                e.GetProperty("stop").GetInt32(), days, new EibiSeasonRule(), Text(e, "station"),
                Text(e, "language"), Text(e, "target"), Text(e, "country"), Text(e, "site")));
        }
        return list;
    }
}
