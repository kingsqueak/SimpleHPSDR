# SimpleHPSDR

A small, clean receive-first UI for OpenHPSDR radios (Protocol 1 and 2), built
on [station-engine](https://github.com/Zeus-SDR/station-engine) with
[Avalonia](https://avaloniaui.net/). Runs on macOS, Windows and Linux.

SimpleHPSDR starts the engine as a child process on a free loopback port with
**native audio** (Core Audio / WASAPI / ALSA via miniaudio). Receive audio and
the microphone never pass through the UI, which keeps latency low and audio
playing while the window is hidden or on another Space.

Not affiliated with or endorsed by the Zeus SDR project.

## Build

Requires the .NET 10 SDK.

```sh
git clone --recurse-submodules https://github.com/kingsqueak/SimpleHPSDR.git
cd SimpleHPSDR
scripts/build-engine.sh          # publishes the engine to artifacts/engine/<rid>
dotnet run --project src/SimpleHPSDR
```

Set `SIMPLEHPSDR_ENGINE` to use an engine binary elsewhere.

## Where things live

Settings, the engine's own database (separate from Zeus's) and `engine.log`
are in the app data folder:

- macOS: `~/Library/Application Support/SimpleHPSDR`
- Linux: `~/.local/share/SimpleHPSDR`
- Windows: `%LOCALAPPDATA%\SimpleHPSDR`

## License

GPL-3.0-or-later. Links `Zeus.Contracts` from station-engine (GPL-2.0-or-later).
