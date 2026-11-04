using System.Net;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Firmware window, opened from the radio picker (so the engine isn't
/// connected). The file is read whole and checked before anything is sent,
/// and the radio is probed again just before the erase: no answer, another
/// MAC or a radio in use stops it. There's no cancel once it starts, because
/// stopping after the erase leaves the radio without gateware.
/// </summary>
public sealed partial class FirmwareViewModel(RadioEntry radio) : ObservableObject
{
    private const long MinBytes = 100_000, MaxBytes = 32_000_000;

    private readonly IPEndPoint _target = new(IPAddress.Parse(radio.Info.IpAddress), FirmwareProgrammer.Port);
    private byte[]? _image;
    private bool _eraseStarted;

    public string RadioText { get; } = $"{radio.Info.BoardId}, {radio.Info.IpAddress}, firmware {radio.Info.FirmwareVersion}";
    public string MacText { get; } = radio.Mac;

    [ObservableProperty] private string _fileName = "No file chosen";
    [ObservableProperty] private string _fileDetail = "";
    [ObservableProperty] private string _fileWarning = "";
    [ObservableProperty] private string _status = "Choose the .rbf file for this radio.";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(FlashCommand))] private bool _accepted;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FlashCommand))]
    [NotifyPropertyChangedFor(nameof(Idle))]
    private bool _busy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _finished;

    public bool Idle => !Busy;
    public bool HasFileWarning => FileWarning.Length > 0;
    partial void OnFileWarningChanged(string value) => OnPropertyChanged(nameof(HasFileWarning));

    /// <summary>Read and check a chosen file. Nothing goes to the radio here.</summary>
    public void LoadFile(string path)
    {
        _image = null;
        FlashCommand.NotifyCanExecuteChanged();
        FileName = Path.GetFileName(path);
        FileDetail = "";
        FileWarning = "";
        try
        {
            var size = new FileInfo(path).Length;
            if (!path.EndsWith(".rbf", StringComparison.OrdinalIgnoreCase))
                Status = "That isn't an .rbf file.";
            else if (size is < MinBytes or > MaxBytes)
                Status = $"That file is {size:N0} bytes, not the size of radio firmware.";
            else
            {
                var image = File.ReadAllBytes(path);
                FileDetail = $"{image.Length:N0} bytes, {FirmwareProgrammer.BlocksFor(image.Length):N0} blocks, " +
                             $"SHA-256 {Convert.ToHexStringLower(SHA256.HashData(image))[..16]}…";
                if (image[0] != 0xFF)
                    Status = "That file doesn't start the way an .rbf does.";
                else
                {
                    if (radio.Info.BoardId.ToString().Contains("Orion", StringComparison.OrdinalIgnoreCase) &&
                        !FileName.Contains("Orion", StringComparison.OrdinalIgnoreCase))
                        FileWarning = "The file name doesn't mention Orion. Check it's for this radio.";
                    _image = image;
                    Status = "Read the warning, then tick the box to enable Flash.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not read the file: {ex.Message}";
        }
        FlashCommand.NotifyCanExecuteChanged();
    }

    private bool CanFlash() => _image is not null && Accepted && !Busy && !Finished;

    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashAsync()
    {
        Busy = true;
        Progress = 0;
        var progress = new Progress<(string Text, double Fraction)>(p =>
        {
            Status = p.Text;
            Progress = p.Fraction * 100;
        });
        try
        {
            // After a failed attempt the radio is already erased, so what it
            // says about itself no longer matters: writing again is the way out.
            if (!_eraseStarted && !await CheckRadioAsync()) return;
            _eraseStarted = true;
            await FirmwareProgrammer.WriteAsync(_target, _image!, progress);
            Progress = 100;
            Finished = true;
            Status = "Firmware written. Power-cycle the radio before using it.";
        }
        catch (Exception ex) when (ex is TimeoutException or System.Net.Sockets.SocketException)
        {
            Status = $"{ex.Message} The radio's firmware is erased or incomplete: don't power it off. " +
                     "Check the network, then press Flash to write it again.";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> CheckRadioAsync()
    {
        Status = "Checking the radio…";
        var probe = await FirmwareProgrammer.ProbeAsync(_target);
        string mac = new(radio.Mac.Where(Uri.IsHexDigit).ToArray());
        if (probe is null)
            Status = "The radio didn't answer. Nothing was erased.";
        else if (probe.Running)
            Status = "The radio is in use by a program. Nothing was erased.";
        else if (!string.Equals(probe.Mac, mac, StringComparison.OrdinalIgnoreCase))
            Status = "A different radio answered at this address. Nothing was erased.";
        else
            return true;
        return false;
    }
}
