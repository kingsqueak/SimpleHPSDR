using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR;

/// <summary>
/// Backup and restore (Config > Station). A backup is one .shbackup file, a ZIP
/// holding the engine's settings database (its own export, which leaves out
/// PureSignal's calibration), the app's settings.json and its TX profiles.
///
/// Restoring stages everything and restarts: the engine stages its part itself
/// and swaps it in when it next starts; the app's files go to a holding folder
/// that <see cref="ApplyPending"/> moves into place at the next start, before
/// anything reads them (the running app would otherwise save over them).
/// </summary>
public static class Backup
{
    public const string Extension = "shbackup";
    private const string Format = "simplehpsdr-backup";
    private const string EngineEntry = "engine.zeusdb";
    private const string SettingsEntry = "settings.json";
    private const string ProfilesPrefix = "tx-profiles/";
    private const string ManifestEntry = "manifest.json";

    private static string DataDir => Engine.EngineHost.DataDir;
    private static string PendingDir => Path.Combine(DataDir, ".pending-app-restore");
    private static string ProfilesDir => Path.Combine(DataDir, "tx-profiles");

    private sealed record Manifest(string Format, int Version, string App, string Created);

    public static string SuggestedName() => $"SimpleHPSDR-{DateTime.Now:yyyy-MM-dd-HHmm}.{Extension}";

    /// <summary>Write a backup to <paramref name="stream"/>. The settings are saved first.</summary>
    public static async Task CreateAsync(StationClient client, Settings settings, Stream stream)
    {
        var engine = await client.ExportEngineBackupAsync();
        settings.Save();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteAsync(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(
            new Manifest(Format, 1, App.Version, DateTime.UtcNow.ToString("O"))));
        await WriteAsync(zip, EngineEntry, engine);
        string settingsPath = Path.Combine(DataDir, "settings.json");
        if (File.Exists(settingsPath)) await WriteAsync(zip, SettingsEntry, await File.ReadAllBytesAsync(settingsPath));
        if (Directory.Exists(ProfilesDir))
            foreach (var f in Directory.GetFiles(ProfilesDir, "*.json"))
                await WriteAsync(zip, ProfilesPrefix + Path.GetFileName(f), await File.ReadAllBytesAsync(f));
    }

    private static async Task WriteAsync(ZipArchive zip, string name, byte[] bytes)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await s.WriteAsync(bytes);
    }

    /// <summary>Check a backup and stage it; the restore finishes when SimpleHPSDR restarts.</summary>
    public static async Task StageRestoreAsync(StationClient client, string path)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("That file isn't a SimpleHPSDR backup, or it's damaged."); }
        using var _ = zip;
        Manifest? manifest;
        try { manifest = zip.GetEntry(ManifestEntry) is { } m ? JsonSerializer.Deserialize<Manifest>(m.Open()) : null; }
        catch (JsonException) { manifest = null; }
        if (manifest?.Format != Format)
            throw new InvalidDataException("That isn't a SimpleHPSDR backup.");
        if (zip.GetEntry(EngineEntry) is not { } engineEntry)
            throw new InvalidDataException("The backup has no radio settings in it.");
        if (zip.GetEntry(SettingsEntry) is { } se)
            using (var s = se.Open()) JsonSerializer.Deserialize<Settings>(s);   // throws if it's damaged

        // The engine stages its part (and checks it) from a file it can read.
        string engineFile = Path.Combine(Path.GetTempPath(), $"simplehpsdr-restore-{Guid.NewGuid():N}.zeusdb");
        try
        {
            engineEntry.ExtractToFile(engineFile);
            await client.ImportEngineBackupAsync(engineFile);
        }
        finally { try { File.Delete(engineFile); } catch (IOException) { } }

        if (Directory.Exists(PendingDir)) Directory.Delete(PendingDir, recursive: true);
        Directory.CreateDirectory(Path.Combine(PendingDir, "tx-profiles"));
        foreach (var e in zip.Entries)
        {
            if (e.FullName == SettingsEntry)
                e.ExtractToFile(Path.Combine(PendingDir, SettingsEntry));
            else if (e.FullName.StartsWith(ProfilesPrefix, StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     && e.Name == Path.GetFileName(e.Name))
                e.ExtractToFile(Path.Combine(PendingDir, "tx-profiles", e.Name));
        }
        File.WriteAllText(Path.Combine(PendingDir, "ready"), "");
    }

    /// <summary>At start, before the settings are read: put a staged restore in place.</summary>
    public static void ApplyPending()
    {
        try
        {
            if (!File.Exists(Path.Combine(PendingDir, "ready"))) return;
            string staged = Path.Combine(PendingDir, SettingsEntry);
            if (File.Exists(staged)) File.Copy(staged, Path.Combine(DataDir, "settings.json"), overwrite: true);
            string stagedProfiles = Path.Combine(PendingDir, "tx-profiles");
            if (Directory.Exists(stagedProfiles) && Directory.GetFiles(stagedProfiles).Length > 0)
            {
                if (Directory.Exists(ProfilesDir)) Directory.Delete(ProfilesDir, recursive: true);
                Directory.CreateDirectory(ProfilesDir);
                foreach (var f in Directory.GetFiles(stagedProfiles))
                    File.Copy(f, Path.Combine(ProfilesDir, Path.GetFileName(f)));
            }
            Directory.Delete(PendingDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Backup restore not applied: {e.Message}");
        }
    }

    /// <summary>Start a new copy that waits for this one to exit, then close this one.</summary>
    public static void Restart(Action shutdown)
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, ["--after", Environment.ProcessId.ToString()]) { UseShellExecute = false });
        shutdown();
    }

    /// <summary>For --after: wait (up to 30 s) for the copy that restarted us to exit.</summary>
    public static void WaitForPrevious(string[] args)
    {
        int i = Array.IndexOf(args, "--after");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try { Process.GetProcessById(pid).WaitForExit(TimeSpan.FromSeconds(30)); }
        catch (ArgumentException) { }   // already gone
    }
}
