using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using SimpleHPSDR.Stations;

namespace SimpleHPSDR.ViewModels;

/// <summary>One row of the station guide.</summary>
public sealed partial class GuideRow(Broadcast b, AirState state, Action<double> tune)
{
    public Broadcast Broadcast { get; } = b;
    public AirState State { get; } = state;
    public string KhzText { get; } = b.Khz.ToString("0.###");
    public string Station => Broadcast.Station;
    public string Language => Broadcast.Language;
    public string Target => Broadcast.Target;
    public bool IsOn => State is AirState.OnAir or AirState.EndingSoon;
    public bool IsSoon => State == AirState.StartingSoon;
    public string StateText => State switch { AirState.OnAir => "On now", AirState.EndingSoon => "Ending", AirState.StartingSoon => "Soon", _ => "" };
    public string LocalText { get; } = b.Stop % 1440 == b.Start % 1440 ? "All day" : $"{Local(b.Start)}-{Local(b.Stop)}";
    public int LocalStart { get; } = (int)DateTime.UtcNow.Date.AddMinutes(b.Start).ToLocalTime().TimeOfDay.TotalMinutes;
    public string Detail { get; } = string.Join(" · ", new[] { b.Country, b.Site, b.Days.Text, $"{Hhmm(b.Start)}-{Hhmm(b.Stop)} UTC" }.Where(s => s != ""));

    [RelayCommand] private void Tune() => tune(Broadcast.Khz);

    private static string Hhmm(int m) => $"{m % 1440 / 60:00}{m % 60:00}";
    private static string Local(int m) => DateTime.UtcNow.Date.AddMinutes(m).ToLocalTime().ToString("HH:mm");
}

/// <summary>The SWL station guide: EiBi's non-digital listings, in local time.</summary>
public sealed partial class StationGuideViewModel : ObservableObject
{
    private const string AllLanguages = "All languages";
    /// <summary>Broadcasts sit on a 5 kHz grid, so 1 kHz either side catches tuning that's a little off.</summary>
    private const double MatchKhz = 1;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private static string Dir => Directory.CreateDirectory(Path.Combine(EngineHost.DataDir, "schedule")).FullName;

    private readonly Settings _settings;
    private readonly Action<double> _tune;
    private readonly DispatcherTimer _clock;
    private List<Broadcast> _all = [];
    private string _sort;
    private bool _ascending;
    private string _shown = "";
    private string _fileNote = "";

    /// <summary>The listing or the clock moved on.</summary>
    public event Action? Changed;
    public bool IsOpen { get; private set; }

    public ObservableCollection<GuideRow> Items { get; } = [];
    public ObservableCollection<string> Languages { get; } = [AllLanguages];

    [ObservableProperty] private string _filter = "Now";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _language = AllLanguages;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _sourceStatus = "";
    [ObservableProperty] private bool _isUpdating;

    public string TimeZoneName => TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
    public bool FilterNow => Filter == "Now";
    public bool FilterToday => Filter == "Today";
    public bool FilterAll => Filter == "All";
    public string KhzHeader => Header("kHz", "kHz");
    public string StatusHeader => Header("Status", "Status");
    public string TimeHeader => Header("Time", "Local time");
    public string StationHeader => Header("Station", "Station");
    public string LanguageHeader => Header("Language", "Language");

    private string Header(string column, string title) => column == _sort ? $"{title} {(_ascending ? "▲" : "▼")}" : title;

    public StationGuideViewModel(Settings settings, Action<double> tune)
    {
        (_settings, _tune) = (settings, tune);
        _sort = settings.GuideSort;
        _ascending = settings.GuideSortAscending;
        Load();
        if (settings.GuideLanguage != "")
        {
            if (!Languages.Contains(settings.GuideLanguage)) Languages.Add(settings.GuideLanguage);
            Language = settings.GuideLanguage;
        }
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Apply();
    }

    /// <summary>The window opened: refresh the listing and keep it current against the clock.</summary>
    public void Start()
    {
        IsOpen = true;
        _clock.Start();
        Apply();
        if (CurrentFile() is not { } f || DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > MaxAge) _ = UpdateAsync();
    }

    public void Stop()
    {
        IsOpen = false;
        _clock.Stop();
        Changed?.Invoke();
    }

    /// <summary>What's on the air at this frequency now, nearest first, one line per station and language.</summary>
    public List<Broadcast> OnAirAt(double khz)
    {
        var now = DateTime.UtcNow;
        return _all.Where(b => Math.Abs(b.Khz - khz) <= MatchKhz && b.StateAt(now) is AirState.OnAir or AirState.EndingSoon)
            .OrderBy(b => Math.Abs(b.Khz - khz))
            .DistinctBy(b => (b.Station, b.Language))
            .ToList();
    }

    private static string FileFor(string season) => Path.Combine(Dir, $"sked-{season}.csv");

    /// <summary>This season's file, else the newest one from an earlier season.</summary>
    private static string? CurrentFile()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = FileFor(EibiSeason.Code(today));
        return File.Exists(now) ? now : null;
    }

    private void Load()
    {
        var file = CurrentFile() ?? Directory.GetFiles(Dir, "sked-*.csv").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null)
        {
            _fileNote = "No schedule downloaded yet.";
            SourceStatus = _fileNote;
            return;
        }
        try
        {
            // EiBi's files are Latin-1.
            _all = EibiSchedule.Parse(File.ReadAllText(file, Encoding.Latin1));
        }
        catch (IOException) { return; }
        var age = File.GetLastWriteTime(file).ToString("d MMM");
        _fileNote = $"From EiBi (eibispace.de), downloaded {age}. Schedules can be out of date.";
        SourceStatus = _fileNote;
        UpdateLanguages();
    }

    /// <summary>Fetch this season's file. A failure keeps the cached copy.</summary>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        var season = EibiSeason.Code(DateOnly.FromDateTime(DateTime.UtcNow));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SimpleHPSDR/{App.Version}");
            var bytes = await http.GetByteArrayAsync($"http://www.eibispace.de/dx/sked-{season}.csv");
            var dest = FileFor(season);
            await File.WriteAllBytesAsync(dest + ".tmp", bytes);
            File.Move(dest + ".tmp", dest, true);
            Load();
            Apply();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            SourceStatus = $"{_fileNote} Couldn't update: {e.Message}";
        }
        finally { IsUpdating = false; }
    }

    [RelayCommand] private void SetFilter(string which) => Filter = which;

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilterNow));
        OnPropertyChanged(nameof(FilterToday));
        OnPropertyChanged(nameof(FilterAll));
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();

    partial void OnLanguageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return; // rebuilding the list clears the selection for a moment
        _settings.GuideLanguage = value == AllLanguages ? "" : value;
        _settings.Save();
        Apply();
    }

    [RelayCommand]
    private void Sort(string column)
    {
        _ascending = column != _sort || !_ascending;
        _sort = column;
        _settings.GuideSort = _sort;
        _settings.GuideSortAscending = _ascending;
        _settings.Save();
        foreach (var name in new[] { nameof(KhzHeader), nameof(StatusHeader), nameof(TimeHeader), nameof(StationHeader), nameof(LanguageHeader) })
            OnPropertyChanged(name);
        Apply();
    }

    private void UpdateLanguages()
    {
        var chosen = Language;
        var found = _all.Select(b => b.Language).Where(l => l != "" && !l.StartsWith('-')).Distinct(StringComparer.OrdinalIgnoreCase);
        if (chosen != AllLanguages) found = found.Append(chosen).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = found.Order(StringComparer.OrdinalIgnoreCase).Prepend(AllLanguages).ToList();
        if (list.SequenceEqual(Languages)) return;
        Languages.Clear();
        foreach (var l in list) Languages.Add(l);
        Language = chosen;
    }

    private void Apply()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var q = Search.Trim();
        var rows = _all.Select(b => new GuideRow(b, b.StateAt(now), _tune));
        var picked = rows.Where(r => Filter switch
            {
                "Now" => r.IsOn || r.IsSoon,
                "Today" => r.Broadcast.RunsOn(today),
                _ => true,
            })
            .Where(r => q == "" || r.Station.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.Target.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.Broadcast.Country.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.Language.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.KhzText.Contains(q))
            .Where(r => Language is AllLanguages or "" || r.Language.Equals(Language, StringComparison.OrdinalIgnoreCase));
        var sorted = Sorted(picked).ToList();
        var shown = string.Join("|", sorted.Select(r => $"{r.Broadcast.Khz}{r.Broadcast.Start}{r.State}{r.Station}"));
        if (shown != _shown)
        {
            _shown = shown;
            Items.Clear();
            foreach (var r in sorted) Items.Add(r);
        }
        Summary = $"{sorted.Count} shown, {_all.Count} listed";
        Changed?.Invoke();
    }

    private IEnumerable<GuideRow> Sorted(IEnumerable<GuideRow> rows)
    {
        var list = rows as IList<GuideRow> ?? rows.ToList();
        IOrderedEnumerable<GuideRow> By<T>(Func<GuideRow, T> key) => _ascending ? list.OrderBy(key) : list.OrderByDescending(key);
        IOrderedEnumerable<GuideRow> ByText(Func<GuideRow, string> key)
        {
            var blankLast = list.OrderBy(r => key(r).Trim() == "");
            return _ascending ? blankLast.ThenBy(key, StringComparer.OrdinalIgnoreCase) : blankLast.ThenByDescending(key, StringComparer.OrdinalIgnoreCase);
        }
        return (_sort switch
        {
            "kHz" => By(r => r.Broadcast.Khz),
            "Time" => By(r => r.LocalStart),
            "Station" => ByText(r => r.Station),
            "Language" => ByText(r => r.Language),
            _ => By(r => r.State switch { AirState.OnAir => 0, AirState.EndingSoon => 1, AirState.StartingSoon => 2, _ => 3 }),
        }).ThenBy(r => r.Broadcast.Khz).ThenBy(r => r.Broadcast.Start);
    }
}
