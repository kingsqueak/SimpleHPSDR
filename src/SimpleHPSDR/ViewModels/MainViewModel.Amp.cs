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
    private bool _ampUnreachableSaid;
    // A second click within this time turns Amp off although the amplifier
    // can't be reached (it is switched off, say).
    private DateTime _ampOffAnywayUntil;

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
                if (DateTime.UtcNow > _ampOffAnywayUntil)
                {
                    _ampOffAnywayUntil = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                    Status = "The amplifier didn't answer, so Amp stays on. If it's switched off, click Amp again now to turn Amp off anyway.";
                    return;
                }
            }
            _ampOffAnywayUntil = default;
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

    /// <summary>Once a second, with the state poll: the Amp button follows an
    /// RF2K-S switched at the amplifier. Operate with Amp off is the dangerous
    /// case (full drive into the amplifier), so that one stops a transmission
    /// to bring the limits down.</summary>
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
                if (!_ampUnreachableSaid)
                    Status = "Can't reach the amplifier: the Amp button isn't following it.";
                _ampUnreachableSaid = true;
                return;
            }
            _ampUnreachableSaid = false;
            if (operating == true && !AmpOn)
            {
                if (IsTransmitting) { MoxOn = false; TunOn = false; }
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        await _client.SetAmpAsync(true);
                        ApplyTxLimits(await _client.GetStateAsync(), levelsToo: true);
                        Status = "The amplifier is in operate, so Amp is on.";
                        break;
                    }
                    catch (Exception) { await Task.Delay(200); }   // still unkeying
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
