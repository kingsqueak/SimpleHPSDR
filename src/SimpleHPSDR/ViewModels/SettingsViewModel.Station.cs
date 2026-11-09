using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One transverter band in Configuration > Station.</summary>
public sealed partial class TransverterRow(TransverterBandDto b) : ObservableObject
{
    private readonly TransverterBandDto _band = b;
    [ObservableProperty] private bool _enabled = b.Enabled;
    [ObservableProperty] private string _name = b.ButtonText;
    [ObservableProperty] private double _loMhz = b.LoOffsetHz / 1e6;
    [ObservableProperty] private double _beginMhz = b.BeginFrequencyHz / 1e6;
    [ObservableProperty] private double _endMhz = b.EndFrequencyHz / 1e6;
    [ObservableProperty] private double _rxGainDb = b.RxGainDb;
    [ObservableProperty] private bool _rxOnly = b.RxOnly;
    [ObservableProperty] private bool _disablePa = b.DisablePa;

    public TransverterBandDto ToBand() => _band with
    {
        Enabled = Enabled, ButtonText = Name, LoOffsetHz = (long)Math.Round(LoMhz * 1e6),
        BeginFrequencyHz = (long)Math.Round(BeginMhz * 1e6), EndFrequencyHz = (long)Math.Round(EndMhz * 1e6),
        RxGainDb = RxGainDb, RxOnly = RxOnly, DisablePa = DisablePa,
    };
}

/// <summary>Configuration > Filters and Station. Only the bypass switches of
/// the RF filters are here: the built-in table for the radio stays in charge.
/// CFC presets for the TX Audio page are at the end.</summary>
public sealed partial class SettingsViewModel
{
    public string[] WindowNames { get; } =
        ["Soft (1k taps)", "Normal (2k taps)", "Sharp (4k taps)", "8k taps", "16k taps", "32k taps", "64k taps", "128k taps", "256k taps"];
    public string[] PhaseNames { get; } = ["Linear", "Minimum"];
    public string[] DiversityOutputs { get; } = ["Combined", "Reference", "Source"];
    public ObservableCollection<string> SerialPorts { get; } = [];
    public ObservableCollection<TransverterRow> Transverters { get; } = [];

    // ---- band plan: kept by the engine, applied as it changes -----------

    private List<BandRegion> _bandRegions = [];
    public ObservableCollection<string> BandPlanNames { get; } = [];
    [ObservableProperty] private int _bandPlanIndex = -1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BandPlanRestricts))] private bool _generalCoverage;

    /// <summary>"Warn only" then "Block transmit".</summary>
    [ObservableProperty] private int _bandPlanActionIndex;
    public bool BandPlanRestricts => !GeneralCoverage;

    private async Task LoadBandPlansAsync()
    {
        _bandRegions = await _client.GetBandRegionsAsync();
        BandPlanNames.Clear();
        foreach (var r in _bandRegions) BandPlanNames.Add(r.DisplayName);
        BandPlanNames.Add(MainViewModel.GeneralCoverageName);
        var current = await _client.GetBandRegionAsync();
        GeneralCoverage = AppSettings?.GeneralCoverage == true;
        BandPlanActionIndex = AppSettings?.BandPlanBlocks == true ? 1 : 0;
        BandPlanIndex = GeneralCoverage ? _bandRegions.Count : _bandRegions.FindIndex(r => r.Id == current);
    }

    partial void OnBandPlanIndexChanged(int value)
    {
        if (_populating || value < 0 || value > _bandRegions.Count) return;
        _ = SetBandPlanAsync(value < _bandRegions.Count ? _bandRegions[value] : null);
    }

    partial void OnBandPlanActionIndexChanged(int value)
    {
        if (_populating) return;
        _ = SetBandPlanAsync(BandPlanIndex < _bandRegions.Count ? _bandRegions[BandPlanIndex] : null);
    }

    /// <summary>A null region is General Coverage, which leaves the engine's
    /// plan where it was for PA calibration.</summary>
    private async Task SetBandPlanAsync(BandRegion? region)
    {
        if (AppSettings is not { } app) return;
        try
        {
            if (region is not null) await _client.SetBandRegionAsync(region.Id);
            bool blocks = BandPlanActionIndex == 1;
            await _client.SetBandPlanGuardIgnoredAsync(region is null || !blocks);
            GeneralCoverage = app.GeneralCoverage = region is null;
            app.BandPlanBlocks = blocks;
            app.Save();
            BandStripChanged?.Invoke();
            Status = region is null ? "General Coverage: no band plan restrictions."
                : $"Band plan: {region.DisplayName}, {(blocks ? "blocks transmit outside it" : "warns outside it")}.";
        }
        catch (Exception ex) { Status = $"Band plan not changed: {ex.Message}"; }
    }

    /// <summary>The band plan or the strip setting changed: redraw the band strip.</summary>
    public event Action? BandStripChanged;
    public bool BandStripOn
    {
        get => AppSettings?.BandStrip ?? true;
        set
        {
            if (AppSettings is null || value == AppSettings.BandStrip) return;
            AppSettings.BandStrip = value;
            AppSettings.Save();
            OnPropertyChanged();
            BandStripChanged?.Invoke();
        }
    }

    /// <summary>How solid the band strip's colours are, in percent (20 to 100).</summary>
    public double BandStripOpacityPercent
    {
        get => Math.Round((AppSettings?.BandStripOpacity ?? 1.0) * 100);
        set
        {
            double v = Math.Clamp(Math.Round(value), 20, 100) / 100.0;
            if (AppSettings is null || Math.Abs(v - AppSettings.BandStripOpacity) < 0.001) return;
            AppSettings.BandStripOpacity = v;
            AppSettings.Save();
            OnPropertyChanged();
        }
    }

    // ---- ID timer: an app setting, saved as it changes ------------------

    public bool IdTimerOn
    {
        get => AppSettings?.IdTimerOn ?? false;
        set
        {
            if (AppSettings is null) return;
            AppSettings.IdTimerOn = value;
            AppSettings.Save();
        }
    }

    public int IdTimerVolume
    {
        get => AppSettings?.IdTimerVolume ?? 30;
        set
        {
            if (AppSettings is null || value == AppSettings.IdTimerVolume) return;
            AppSettings.IdTimerVolume = value;
            AppSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IdVolumeText));
        }
    }

    public string IdVolumeText => $"{IdTimerVolume}%";

    public int TxWaterfallRangeDb
    {
        get => AppSettings?.TxWaterfallRangeDb ?? 70;
        set
        {
            value = Math.Clamp(value, 0, 80);
            if (AppSettings is null || value == AppSettings.TxWaterfallRangeDb) return;
            AppSettings.TxWaterfallRangeDb = value;
            AppSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TxWaterfallRangeText));
        }
    }

    public string TxWaterfallRangeText => TxWaterfallRangeDb == 0 ? "Off" : $"{TxWaterfallRangeDb} dB";

    public string IdSoundName => AppSettings?.IdTimerWav is { } wav ? Path.GetFileName(wav) : "Built-in beeps";
    public bool HasIdSoundFile => AppSettings?.IdTimerWav is not null;

    public void SetIdSound(string? path)
    {
        if (AppSettings is null) return;
        AppSettings.IdTimerWav = path;
        AppSettings.Save();
        IdSoundStatus = "";
        OnPropertyChanged(nameof(IdSoundName));
        OnPropertyChanged(nameof(HasIdSoundFile));
    }

    [RelayCommand]
    private void UseIdBeeps() => SetIdSound(null);

    [ObservableProperty] private string _idSoundStatus = "";

    [RelayCommand]
    private async Task TestIdSoundAsync() =>
        IdSoundStatus = await Engine.IdReminder.PlayAsync(AppSettings?.IdTimerWav, IdTimerVolume) ?? "";

    public string[] ThemeNames { get; } = ["System", "Dark", "Light"];
    public int ThemeIndex
    {
        get => Math.Max(0, Array.IndexOf(ThemeNames, AppSettings?.Theme));
        set
        {
            if (AppSettings is null || value < 0) return;
            AppSettings.Theme = ThemeNames[value];
            AppSettings.Save();
            App.ApplyTheme(AppSettings.Theme);
        }
    }

    public string[] LayoutNames => Platform.LayoutMode.Names;
    public int LayoutIndex
    {
        get => Math.Max(0, Array.IndexOf(Platform.LayoutMode.Names, AppSettings?.Layout));
        set
        {
            if (AppSettings is null || value < 0) return;
            AppSettings.Layout = Platform.LayoutMode.Names[value];
            AppSettings.Save();
            Platform.LayoutMode.NotifySettingChanged();
            OnPropertyChanged(nameof(LayoutStatus));
        }
    }
    /// <summary>What the layout is now and why, under the choice.</summary>
    public string LayoutStatus => AppSettings is null ? "" :
        Platform.LayoutMode.Describe(AppSettings);
    public bool LayoutOverridden => Platform.LayoutMode.Override is not null;

    /// <summary>Only a Linux arm64 computer can be a G2's own.</summary>
    public bool CanBeG2Computer => OperatingSystem.IsLinux()
        && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
    /// <summary>The engine reads this at startup, so a change restarts it.</summary>
    public bool G2Computer
    {
        get => AppSettings?.G2Computer == true;
        set
        {
            if (AppSettings is null || value == AppSettings.G2Computer) return;
            AppSettings.G2Computer = value;
            AppSettings.Save();
            OnPropertyChanged();
            if (RestartEngine is { } restart) _ = restart();
        }
    }

    public string[] RadioModelNames => RadioModels.Names;
    /// <summary>Only the OrionMkII family needs telling apart.</summary>
    [ObservableProperty] private bool _showRadioModel;
    [ObservableProperty] private int _radioModelIndex;
    private int _modelWas, _modelOrig;

    [ObservableProperty] private int _rxWindowIndex, _txWindowIndex, _rxPhaseIndex, _txPhaseIndex;
    [ObservableProperty] private bool _rxBypassAll, _rxBypassOnTx, _rxBypassOnPs;
    [ObservableProperty] private string _rfFilterText = "";

    [ObservableProperty] private bool _pttInputOn, _serialPttOn, _serialSenseCts, _serialSenseDsr;
    [ObservableProperty] private string? _serialPttPort;
    [ObservableProperty] private string _serialPttState = "";
    [ObservableProperty] private bool _transverterOn;
    /// <summary>Transverter settings applied: the main window rebuilds its band list.</summary>
    public event Action<TransverterSettingsDto>? TransvertersSaved;
    [ObservableProperty] private bool _diversityOn;
    [ObservableProperty] private double _diversityGain, _diversityPhase;
    [ObservableProperty] private int _diversityOutputIndex;

    [ObservableProperty] private string? _selectedCfcPreset;
    [ObservableProperty] private string _cfcPresetName = "";
    public ObservableCollection<string> CfcPresets { get; } = [];
    private List<Engine.StationClient.CfcPresetDto> _cfcPresets = [];

    private sealed record FilterValues(int RxW, int TxW, int RxP, int TxP, bool All, bool OnTx, bool OnPs);
    private sealed record StationValues(bool Ptt, bool Serial, string? Port, bool Cts, bool Dsr);
    private sealed record DiversityValues(bool On, double Gain, double Phase, int Output);
    private FilterValues? _filtersWas, _filtersOrig;
    private StationValues? _stationWas, _stationOrig;
    private DiversityValues? _diversityWas, _diversityOrig;
    private RfFilterSettingsDto? _rf;
    private TransverterSettingsDto? _transverter, _transverterOrig;

    private async Task LoadFiltersStationAsync(StateDto s)
    {
        var model = await _client.GetRadioVariantAsync();
        _modelWas = _modelOrig = RadioModels.IndexOf(model.Variant);
        RadioModelIndex = _modelWas;
        ShowRadioModel = await _client.GetConnectedBoardAsync() == RadioModels.BoardName;
        _rf = await _client.GetRfFiltersAsync();
        _filtersWas = _filtersOrig = new FilterValues((int)s.RxFilterWindow, (int)s.TxFilterWindow,
            (int)s.RxFilterPhase, (int)s.TxFilterPhase, _rf.RxBypassAll, _rf.RxBypassOnTx, _rf.RxBypassOnPureSignal);
        ShowFilters(_filtersWas);
        RfFilterText = $"{_rf.BoardFamily}. Receive filter now: {_rf.Active.Rx1Label}.";

        var serial = await _client.GetSerialPttAsync();
        SerialPorts.Clear();
        foreach (var p in serial.AvailablePorts) SerialPorts.Add(p);
        _stationWas = _stationOrig = new StationValues(await _client.GetPttInputAsync(), serial.Config.Enabled,
            serial.Config.PortName.Length > 0 ? serial.Config.PortName : null, serial.Config.SenseCts, serial.Config.SenseDsr);
        ShowStation(_stationWas);
        ShowSerialState(serial);

        _transverter = _transverterOrig = await _client.GetTransverterAsync();
        ShowTransverter(_transverter);

        var d = s.Diversity ?? new DiversityConfig();
        _diversityWas = _diversityOrig = new DiversityValues(d.Enabled, d.Gain, d.PhaseDeg, (int)d.Output);
        ShowDiversity(_diversityWas);
    }

    private void ShowFilters(FilterValues f) =>
        (RxWindowIndex, TxWindowIndex, RxPhaseIndex, TxPhaseIndex, RxBypassAll, RxBypassOnTx, RxBypassOnPs) =
        (f.RxW, f.TxW, f.RxP, f.TxP, f.All, f.OnTx, f.OnPs);

    private void ShowStation(StationValues v) =>
        (PttInputOn, SerialPttOn, SerialPttPort, SerialSenseCts, SerialSenseDsr) = (v.Ptt, v.Serial, v.Port, v.Cts, v.Dsr);

    private void ShowDiversity(DiversityValues d) =>
        (DiversityOn, DiversityGain, DiversityPhase, DiversityOutputIndex) = (d.On, d.Gain, d.Phase, d.Output);

    private void ShowTransverter(TransverterSettingsDto t)
    {
        TransverterOn = t.Enabled;
        Transverters.Clear();
        foreach (var b in t.Bands ?? []) Transverters.Add(Watch(new TransverterRow(b), Group.Transverter));
    }

    private void ShowSerialState(SerialPttStatus s) =>
        SerialPttState = s.Error is { Length: > 0 } e ? $"Error: {e}"
            : !s.Config.Enabled ? "Off" : s.PortOpen ? "Port open" : "Port not open";

    private void RestoreFiltersStationFields()
    {
        if (_filtersOrig is { } f) ShowFilters(f);
        if (_stationOrig is { } v) ShowStation(v);
        RadioModelIndex = _modelOrig;
        if (_diversityOrig is { } d) ShowDiversity(d);
        if (_transverterOrig is { } t) ShowTransverter(t);
    }

    private async Task ApplyFiltersAsync()
    {
        if (_filtersWas is not { } was || _rf is null) return;
        var now = new FilterValues(RxWindowIndex, TxWindowIndex, RxPhaseIndex, TxPhaseIndex, RxBypassAll, RxBypassOnTx, RxBypassOnPs);
        if (now.RxP != was.RxP) await _client.SetRxFilterPhaseAsync((FilterPhaseMode)now.RxP);
        if (now.TxP != was.TxP) await _client.SetTxFilterPhaseAsync((FilterPhaseMode)now.TxP);
        if (now.RxW != was.RxW) await _client.SetRxFilterWindowAsync((BandpassWindow)now.RxW);
        if (now.TxW != was.TxW) await _client.SetTxFilterWindowAsync((BandpassWindow)now.TxW);
        if ((now.All, now.OnTx, now.OnPs) != (was.All, was.OnTx, was.OnPs))
            await _client.SetRfFiltersAsync(new RfFilterSettingsSetRequest(
                _rf.CustomMatrixEnabled, now.All, now.OnTx, now.OnPs, _rf.Profiles));
        // With linear phase the engine limits TX to 16k taps: show what it kept.
        var s = await _client.GetStateAsync();
        _filtersWas = now with { RxW = (int)s.RxFilterWindow, TxW = (int)s.TxFilterWindow };
        if (_filtersWas.TxW != now.TxW)
        {
            _populating = true;
            TxWindowIndex = _filtersWas.TxW;
            _populating = false;
            Status = "With linear phase, TX stops at 16k taps. Choose minimum phase to go higher.";
        }
    }

    private async Task ApplyStationAsync()
    {
        if (RadioModelIndex != _modelWas)
        {
            await _client.SetRadioVariantAsync((OrionMkIIVariant)RadioModelIndex);
            _modelWas = RadioModelIndex;
            await LoadHardwareAsync();
            Status = "Radio model set. Choose the radio again to reconnect with it.";
        }
        if (_stationWas is not { } was) return;
        var now = new StationValues(PttInputOn, SerialPttOn, SerialPttPort, SerialSenseCts, SerialSenseDsr);
        if (now.Ptt != was.Ptt) await _client.SetPttInputAsync(now.Ptt);
        if (now with { Ptt = was.Ptt } != was)
        {
            await _client.SetSerialPttAsync(new SerialPttConfig(now.Serial, now.Port ?? "", now.Cts, now.Dsr));
            await Task.Delay(300);
            ShowSerialState(await _client.GetSerialPttAsync());
        }
        _stationWas = now;
    }

    private async Task ApplyTransverterAsync()
    {
        if (_transverter is not { } was) return;
        var bands = Transverters.Select(r => r.ToBand()).ToList();
        if (TransverterOn == was.Enabled && bands.SequenceEqual(was.Bands ?? [])) return;
        await _client.SetTransverterAsync(new TransverterSettingsSetRequest(TransverterOn, was.IfFrequencyHz, was.RfFrequencyHz,
            Bands: bands, ActiveBandId: was.ActiveBandId));
        _transverter = was with { Enabled = TransverterOn, Bands = bands };
        if (AppSettings is { } app)
        {
            app.TransverterOn = TransverterOn;
            app.Save();
        }
        TransvertersSaved?.Invoke(_transverter);
    }

    private async Task ApplyDiversityAsync()
    {
        if (_diversityWas is not { } was) return;
        var now = new DiversityValues(DiversityOn, DiversityGain, DiversityPhase, DiversityOutputIndex);
        if (now == was) return;
        await _client.SetDiversityAsync(new DiversitySetRequest(now.On, now.Gain, now.Phase, Output: (DiversityOutput)now.Output));
        _diversityWas = now;
    }

    // ---- CFC presets ----------------------------------------------------

    private async Task LoadCfcPresetsAsync()
    {
        _cfcPresets = await _client.GetCfcPresetsAsync();
        CfcPresets.Clear();
        foreach (var p in _cfcPresets) CfcPresets.Add(p.Name);
    }

    /// <summary>Choosing a preset loads it at once.</summary>
    partial void OnSelectedCfcPresetChanged(string? value)
    {
        if (_populating || _cfcPresets.FirstOrDefault(p => p.Name == value) is not { } preset) return;
        CfcPresetName = preset.Name;
        _ = LoadCfcPresetAsync(preset.Config);
    }

    private async Task LoadCfcPresetAsync(CfcConfig config)
    {
        try
        {
            await _client.SetCfcAsync(config);
            var s = await _client.GetStateAsync();
            _populating = true;
            try { PopulateTxAudio(s); }
            finally { _populating = false; }
        }
        catch (Exception ex) { Status = $"Could not load the preset: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveCfcPresetAsync()
    {
        var name = CfcPresetName.Trim();
        if (name.Length == 0 || _txLoaded is not { } was)
        {
            Status = "Type a name for the preset first.";
            return;
        }
        try
        {
            await _client.SaveCfcPresetAsync(name, BuildCfc(was));
            _populating = true;
            try
            {
                await LoadCfcPresetsAsync();
                SelectedCfcPreset = name;
            }
            finally { _populating = false; }
            Status = $"CFC preset \"{name}\" saved.";
        }
        catch (Exception ex) { Status = $"Could not save the preset: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteCfcPresetAsync()
    {
        if (SelectedCfcPreset is not { } name) return;
        try
        {
            await _client.DeleteCfcPresetAsync(name);
            _populating = true;
            try
            {
                await LoadCfcPresetsAsync();
                SelectedCfcPreset = null;
            }
            finally { _populating = false; }
            CfcPresetName = "";
        }
        catch (Exception ex) { Status = $"Could not delete the preset: {ex.Message}"; }
    }
}
