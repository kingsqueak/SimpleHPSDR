using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SimpleHPSDR.Engine;

/// <summary>
/// An RF2K-S amplifier's own network interface (HTTP on port 8080): whether
/// it is in operate or standby, and switching between them. In standby the
/// amplifier passes the radio straight through even while it is keyed, which
/// is what lets the Amp button take it out of line when the key cable comes
/// from the radio's PTT OUT jack.
/// </summary>
public static class Rf2kAmp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>"192.168.16.4" or "192.168.16.4:8080" to the interface's address; null if it isn't one.</summary>
    private static Uri? Url(string? address, string path)
    {
        var text = (address ?? "").Trim();
        int scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        text = text.TrimEnd('/');
        var parts = text.Split(':');
        if (parts.Length is 0 or > 2 || Uri.CheckHostName(parts[0]) == UriHostNameType.Unknown) return null;
        // Numbers and dots must be a whole four-part address. "192.168.16" is
        // a valid short form of another address, and a half-typed one must
        // never be contacted.
        if (parts[0].All(c => char.IsAsciiDigit(c) || c == '.')
            && (parts[0].Split('.') is not { Length: 4 } quad || !quad.All(q => byte.TryParse(q, out _)))) return null;
        int port = 8080;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535)) return null;
        return new Uri($"http://{parts[0]}:{port}/{path}");
    }

    /// <summary>True in operate, false in standby, null when it can't be reached or doesn't say.</summary>
    public static async Task<bool?> IsOperatingAsync(string? address)
    {
        if (Url(address, "operate-mode") is not { } url) return null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            return doc.RootElement.TryGetProperty("operate_mode", out var mode) ? mode.GetString() switch
            {
                "OPERATE" => true,
                "STANDBY" => false,
                _ => null,
            } : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Asks for operate or standby and reads it back. True only when
    /// the amplifier then reports that state.</summary>
    public static async Task<bool> SetOperatingAsync(string? address, bool operate)
    {
        if (Url(address, "operate-mode") is not { } url) return false;
        try
        {
            string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["operate_mode"] = operate ? "OPERATE" : "STANDBY" });
            using var reply = await Http.PutAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (Exception) { return false; }
        // It may take a moment to change over.
        for (int i = 0; i < 6; i++)
        {
            if (await IsOperatingAsync(address) == operate) return true;
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>What answers at the address, for Config's Test button; null if nothing does.</summary>
    public static async Task<string?> DescribeAsync(string? address)
    {
        if (Url(address, "info") is not { } url) return null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            string device = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
            if (device.Length == 0) return null;
            string state = await IsOperatingAsync(address) switch { true => "in operate", false => "in standby", null => "state not reported" };
            return $"Found: {device}, {state}.";
        }
        catch (Exception) { return null; }
    }
}
