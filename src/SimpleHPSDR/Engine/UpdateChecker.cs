using System.Net.Http.Headers;
using System.Text.Json;

namespace SimpleHPSDR.Engine;

/// <summary>
/// Is there a newer SimpleHPSDR? GitHub's latest release, compared with this version,
/// once at startup and from Help > Check for Updates. Nothing is downloaded or
/// installed, and nothing is sent but the request itself.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/kingsqueak/SimpleHPSDR/releases";
    private const string LatestApi = "https://api.github.com/repos/kingsqueak/SimpleHPSDR/releases/latest";

    public sealed record Release(string Version, string Page);

    /// <summary>The latest release, or null when GitHub couldn't be reached.</summary>
    public static async Task<Release?> FetchLatestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SimpleHPSDR", App.Version.Length > 0 ? App.Version : "0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi));
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } t) return null;
            string page = root.TryGetProperty("html_url", out var url) && url.GetString() is { Length: > 0 } u ? u : ReleasesPage;
            return new Release(t.TrimStart('v', 'V'), page);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"0.5.10" against "0.5.9", number by number.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => [.. v.Trim().TrimStart('v', 'V').Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0)];
        int[] a = Parts(candidate), b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
