# SimpleHPSDR

A clean, simple interface for OpenHPSDR radios, built on
[Station Engine](https://github.com/Zeus-SDR/station-engine) with
[Avalonia](https://avaloniaui.net/). The everyday controls stay on screen and
the rest is a click or right-click away.

![SimpleHPSDR on 40 m](docs/main.png)

Audio runs natively in the engine, so receive audio and the microphone never
pass through the interface. That keeps latency low, and audio keeps playing
while the window is hidden or on another Space.

Developed on an ANAN 7000DLE MkII over Protocol 2. macOS and Linux packages
are available. A Windows x64 zip is built too, with ASIO under Config > Audio,
but it hasn't been tried with a radio yet.

Not affiliated with or endorsed by the Zeus SDR project.

## Features

- **Panadapter and waterfall:** drag to recentre, a zoom slider, an adjustable
  split between spectrum and waterfall, POP waterfall contrast, and a frequency
  readout that follows the pointer.
- **Filter close-up:** 0 to 6000 Hz of received audio with draggable filter edges.
- **Two slices:** on one panadapter or each with its own, including cross-band
  and split, with an AF flag for each on one slider.
- **Band and mode memory:** each pair, such as 40 m DIGU, keeps its own
  frequency, filter, AGC, drive, mic gain and noise settings.
- **Noise control:** NB1 and NB2, NR1 to NR4 and neural NNR, with their settings
  on right-click.
- **Transmit:** PureSignal with one-button calibration, CFC, leveler, EQ, phase
  rotator, monitor and TX profiles that include PureSignal settings.
- **Station:** CAT, TCI, antennas, band outputs, PA calibration, transverters and
  amplifier timing.
- **Firmware loader** for Protocol 2 radios.
- **Light and dark themes**, following the system.

Most controls have more behind a right-click. See **Help** in the menu.

## Install

Download a package from [Releases](https://github.com/kingsqueak/SimpleHPSDR/releases).

**macOS 14 or later:** open the DMG and drag SimpleHPSDR to Applications. The app
isn't signed by Apple, so the first time, run:

```sh
xattr -dr com.apple.quarantine /Applications/SimpleHPSDR.app
```

**Linux (x86-64 and arm64):**

```sh
sudo apt install ./simplehpsdr_*.deb        # Ubuntu 26.04, Debian
sudo dnf install ./simplehpsdr-*.rpm        # Fedora
sudo pacman -U ./simplehpsdr-*.pkg.tar.zst  # Arch
```

**Windows 10 or 11 (x64), preview:** unzip the folder anywhere you can write to
and run `SimpleHPSDR.exe`; nothing needs installing, and the .NET and Visual C++
runtimes are included. The app isn't signed, so SmartScreen warns the first time:
choose More info, then Run anyway. If Windows Firewall asks about
`StationEngine.exe`, allow it on private networks, or the radio won't be found.
To use ASIO, install your audio interface's driver, then pick ASIO under
Config > Audio. The driver must be able to run at 48 kHz. The Windows build hasn't
been tried with a radio yet, so please report what you find.

Then choose your radio. Several radios report the same ID, so pick your model in
the radio picker or in Config > Radio. It sets the S-meter calibration and the PA
and PureSignal defaults.

## Build

Requires the .NET 10 SDK.

```sh
git clone --recurse-submodules https://github.com/kingsqueak/SimpleHPSDR.git
cd SimpleHPSDR
scripts/build-engine.sh          # publishes the engine to artifacts/engine/<rid>
dotnet run --project src/SimpleHPSDR
```

`scripts/make-dmg.sh`, `scripts/make-linux.sh` and `scripts/make-windows.sh` build the packages. Set
`SIMPLEHPSDR_ENGINE` to use an engine binary elsewhere.

## Where things live

Settings, the engine's own database (separate from Zeus's) and `engine.log` are
in the app data folder:

- macOS: `~/Library/Application Support/SimpleHPSDR`
- Linux: `~/.local/share/SimpleHPSDR`
- Windows: `%LOCALAPPDATA%\SimpleHPSDR`

## Credits

SimpleHPSDR stands on Station Engine, the radio and DSP engine from the Zeus SDR
project, whose principal developers are Douglas J. Cerrato, KB2UKA, and Christian
Suarez, N9WAR. WDSP is by Warren Pratt, NR0V. The firmware loader follows TAPR's
HPSDRProgrammer and deskHPSDR by Heiko Amft, DL1BZ.

## License

GPL-3.0-or-later. Station Engine, which the app runs and links, is
GPL-2.0-or-later.
