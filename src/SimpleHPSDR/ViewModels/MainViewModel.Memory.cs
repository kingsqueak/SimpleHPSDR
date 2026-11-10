using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Band and mode memory. Each pair, such as 40 m DIGU, keeps its own
/// frequency, filter, AGC, drive, tune, mic gain, NR and NB. The settings are
/// saved on leaving a pair and come back on returning; a pair not used yet
/// keeps what's set now. Choosing a band goes to the mode last used there, at
/// its frequency. Choosing a mode leaves the dial alone, as on a radio.
/// Nothing changes while transmitting.</summary>
public sealed partial class MainViewModel
{
    private string MemoryKey(string band, RxMode mode) => $"{band}|{mode}";

    private static string BandOf(long hz) => SimpleHPSDR.Band.Find(hz)?.Name ?? "GEN";

    private void RememberBandMode()
    {
        if (!IsConnected || IsTransmitting) return;
        string band = BandOf(VfoHz);
        var nr = NoiseReductions.FirstOrDefault(c => c.IsSelected)?.Value as NrMode? ?? NrMode.Off;
        var nb = NoiseBlankers.FirstOrDefault(c => c.IsSelected)?.Value as NbMode? ?? NbMode.Off;
        _settings.ModeMemories[MemoryKey(band, Mode)] = new ModeMemory(
            VfoHz, FilterLowHz, FilterHighHz, AgcAuto, (int)_agcMode, AgcTopDb, AgcFixedGainDb,
            DrivePct, TunePct, MicGainDb, (int)nr, (int)nb);
        _settings.BandLastMode[band] = (int)Mode;
        _settings.Save();
    }

    private async Task SelectBandAsync(Band band)
    {
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before changing band.";
            return;
        }
        if (VfoLocked)
        {
            Status = "VFO is locked.";
            return;
        }
        RememberBandMode();
        if (band.Transverter) await Try(() => _client!.SelectTransverterBandAsync(band.XvtrId));
        var mode = _settings.BandLastMode.TryGetValue(band.Name, out int last) ? (RxMode)last
            : Mode is RxMode.LSB or RxMode.USB ? (band.DefaultHz < 10_000_000 ? RxMode.LSB : RxMode.USB)
            : Mode;
        var memory = _settings.ModeMemories.GetValueOrDefault(MemoryKey(band.Name, mode));
        TuneTo(memory is { } m && band.Contains(m.Hz) ? m.Hz
            : _settings.BandMemory.TryGetValue(band.Name, out long hz) && band.Contains(hz) ? hz
            : band.DefaultHz);
        if (mode != Mode) await ChangeModeAsync(mode);
        await RecallAsync(memory);
    }

    private async Task SelectModeAsync(RxMode mode)
    {
        if (!IsConnected) return;
        if (IsTransmitting)
        {
            Status = "Unkey before changing mode.";
            return;
        }
        RememberBandMode();
        if (mode == RxMode.FreeDv && !await PrepareFreeDvAsync()) return;
        await ChangeModeAsync(mode);
        var memory = _settings.ModeMemories.GetValueOrDefault(MemoryKey(BandOf(VfoHz), mode));
        await RecallAsync(memory);
    }

    private async Task RecallAsync(ModeMemory? m)
    {
        if (m is null || IsTransmitting) return;
        await ApplyFilterAsync(m.FilterLowHz, m.FilterHighHz);
        DrivePct = m.DrivePct;
        TunePct = m.TunePct;
        MicGainDb = m.MicGainDb;
        if ((NrMode)m.NrMode != NoiseReductions.FirstOrDefault(c => c.IsSelected)?.Value as NrMode?)
            await SelectNrAsync((NrMode)m.NrMode);
        if ((NbMode)m.NbMode != NoiseBlankers.FirstOrDefault(c => c.IsSelected)?.Value as NbMode?)
            await SelectNbAsync((NbMode)m.NbMode);
        await Try(async () =>
        {
            var mode = (AgcMode)m.AgcMode;
            var agc = new AgcConfig(mode, FixedGainDb: m.AgcFixedDb);
            // Setting AGC-T turns auto AGC off, so it goes first.
            if (mode != AgcMode.Fixed) await _client!.SetAgcGainAsync(m.AgcTopDb);
            await _client!.SetAgcAsync(agc);
            await _client.SetAutoAgcAsync(m.AgcAuto);
            if (SliceBEnabled)
                await _client.SetReceiverAsync(1, new ReceiverSetRequest(Agc: agc, AutoAgcEnabled: m.AgcAuto));
            UpdateAgc(await _client.GetStateAsync());
        });
    }
}
