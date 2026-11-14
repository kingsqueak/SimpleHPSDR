using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// The Amp button. It always switches the engine's amplifier state (the Drive
/// and Tune maximums and which remembered levels are used). Config > Amp says
/// what else it switches so the amplifier itself is in or out of line:
/// nothing, a radio output wired to the amplifier's key input (the engine
/// drives it), or an RF2K-S over the network (here).
/// </summary>
public sealed partial class MainViewModel
{
    private bool AmpIsRf2k => _settings.AmpSwitch == "Rf2k";

    /// <summary>The output the engine should key the amplifier with, from the settings.</summary>
    private int WantedAmpKeyPin => _settings.AmpSwitch == "Output" ? Math.Clamp(_settings.AmpOutputPin, 1, 7) : 0;

    // The watch leaves the button alone for a moment after a click, while the
    // amplifier changes over.
    private DateTime _ampSwitchedAt;
    private static readonly TimeSpan AmpSettle = TimeSpan.FromSeconds(4);
    private bool _ampWatchBusy;

    /// <summary>The RF2K-S isn't answering, so the button can't follow it or
    /// take it out of line. Amp's popup then offers to turn Amp off on the
    /// operator's word that the amplifier is switched off.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AmpOffByHandShown))] private bool _ampUnreachable;
    public bool AmpOffByHandShown => AmpUnreachable && AmpOn && AmpIsRf2k;

    [RelayCommand]
    private async Task ToggleAmpAsync()
    {
        if (!IsConnected || _client is null) return;
        if (IsTransmitting)
        {
            Status = "Stop transmitting before switching Amp.";
            return;
        }
        bool on = !AmpOn;
        _ampSwitchedAt = DateTime.UtcNow;
        try
        {
            // Off lifts the limits, so the amplifier has to be out of line first.
            if (AmpIsRf2k && !on && !await Rf2kAmp.SetOperatingAsync(_settings.AmpAddress, false))
            {
                AmpUnreachable = await Rf2kAmp.IsOperatingAsync(_settings.AmpAddress) is null;
                Status = AmpUnreachable
                    ? "The amplifier didn't answer, so Amp stays on. If the amplifier is switched off, right-click Amp to turn Amp off."
                    : "The amplifier didn't go to standby, so Amp stays on.";
                return;
            }
            await _client.SetAmpAsync(on);
            // The engine has lowered the levels to the new maximums and
            // recalled the ones last used in this state: show them.
            ApplyTxLimits(await _client.GetStateAsync(), levelsToo: true);
            if (AmpIsRf2k && on && !await Rf2kAmp.SetOperatingAsync(_settings.AmpAddress, true))
                Status = "Amp is on, but the amplifier didn't go to operate. Check the amplifier.";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { _ampSwitchedAt = DateTime.UtcNow; }
    }

    /// <summary>Amp's popup, while the amplifier isn't answering: turn Amp
    /// off on the operator's word that the amplifier is switched off. Nothing
    /// can check that, which is why it is its own labelled button.</summary>
    [RelayCommand]
    private async Task AmpOffByHandAsync()
    {
        if (!IsConnected || _client is null || !AmpOn) return;
        if (IsTransmitting)
        {
            Status = "Stop transmitting before switching Amp.";
            return;
        }
        _ampSwitchedAt = DateTime.UtcNow;
        try
        {
            await _client.SetAmpAsync(false);
            ApplyTxLimits(await _client.GetStateAsync(), levelsToo: true);
            Status = "Amp is off on your word that the amplifier is switched off. It will follow the amplifier again when it answers.";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { _ampSwitchedAt = DateTime.UtcNow; }
    }

    partial void OnAmpOnChanged(bool value) => OnPropertyChanged(nameof(AmpOffByHandShown));

    /// <summary>Once a second, with the state poll: the Amp button follows an
    /// RF2K-S switched at the amplifier. Operate with Amp off is the dangerous
    /// case (full drive into the amplifier): the engine brings the limits
    /// down at once, in the middle of an over if need be.</summary>
    private async Task WatchAmpAsync()
    {
        if (!AmpIsRf2k || _client is null || _ampWatchBusy || DateTime.UtcNow - _ampSwitchedAt < AmpSettle) return;
        _ampWatchBusy = true;
        try
        {
            bool? operating = await Rf2kAmp.IsOperatingAsync(_settings.AmpAddress);
            if (!IsConnected || _client is null || DateTime.UtcNow - _ampSwitchedAt < AmpSettle) return;
            if (operating is null)
            {
                if (!AmpUnreachable)
                    Status = "Can't reach the amplifier: the Amp button isn't following it.";
                AmpUnreachable = true;
                return;
            }
            AmpUnreachable = false;
            if (operating == true && !AmpOn)
            {
                try
                {
                    await _client.SetAmpAsync(true);
                    ApplyTxLimits(await _client.GetStateAsync(), levelsToo: true);
                    Status = "The amplifier is in operate, so Amp is on.";
                }
                catch (Exception ex)
                {
                    // Not left unsaid: full drive may be going into the amplifier.
                    Status = $"The amplifier is in operate but Amp couldn't be turned on: {ex.Message}";
                }
            }
            else if (operating == false && AmpOn && !IsTransmitting)
            {
                await _client.SetAmpAsync(false);
                ApplyTxLimits(await _client.GetStateAsync(), levelsToo: true);
                Status = "The amplifier is in standby, so Amp is off.";
            }
        }
        catch (Exception) { }   // tried again on the next tick
        finally { _ampWatchBusy = false; }
    }
}
