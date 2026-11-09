# Zeus dependencies in our engine fork

Places in `external/station-engine` (our fork, branch `simplehpsdr`) that assume the
Zeus app, Zeus Link or Zeus's own servers. Surveyed 2026-11-08 on v2.0.68-simplehpsdr.1.
Each needs a decision: **change**, **leave**, or **remove**.

An idle engine (no radio, 30 s) made no outside connections and listened only on
127.0.0.1, so nothing below phones home on its own.

## A. Files shared with an installed Zeus

SimpleHPSDR already moves the settings database (`ZEUS_PREFS_PATH`) and the plugin
folder (`ZEUS_PLUGINS_PATH`). These still go to Zeus's own folder,
`~/Library/Application Support/Zeus` (macOS), `~/.local/share/Zeus` (Linux),
`%LOCALAPPDATA%\Zeus` (Windows):

| # | What | Where | Effect |
|---|------|-------|--------|
| A1 | WDSP FFT wisdom file | `Zeus.Dsp/Wdsp/WdspWisdomInitializer.cs:296` | Shared with Zeus. Different WDSP/FFTW builds may each rebuild it (the 8-minute first start). |
| A2 | LAN HTTPS certificate | `Station.Engine.Hosting/LanCertificate.cs:214` | Shared with Zeus. |
| A3 | NR3 models | `Station.Engine.Hosting/Nr3ModelStore.cs:78` | Shared with Zeus. |
| A4 | Default profile folder when no `ZEUS_PREFS_PATH` | `Station.Engine.Hosting/PrefsDbPath.cs:41` | Not used by us (we set the variable). |
| A5 | WSPR work folder on Windows | `Station.Engine.Hosting/Wspr/WsprSlotDecoder.cs:262` (`ProgramData\ZeusSDR\wspr`) | Shared with Zeus. |
| A6 | Backup/restore also collects Zeus Link files (`zeus-link-product.db`, `ZeusProduct/logbook`, `Zeus/Product/plugins/*`, voyeur settings) | `DatabaseBackupPolicy.cs:140`, `UnifiedDatabaseBackup.cs:405` | A backup made by our engine could include, and a restore overwrite, Zeus's files. |
| A7 | Windows firewall rule name `ZeusSDR (HPSDR receive)` | `WindowsFirewallService.cs:63` | Already on the to-do list: our grant can replace Zeus's rule. |

Proposal: one fork patch adding a data-root variable (for example `ZEUS_DATA_DIR`) used by
A1-A3 and A5, set by SimpleHPSDR to its own data folder. A6 off when it's set. A7 a
SimpleHPSDR rule name.

## B. Zeus web app and Zeus Link hooks

| # | What | Where | Effect |
|---|------|-------|--------|
| B1 | Transverter on/off kept in a Zeus web-UI workspace | `RadioTuningEndpoints.cs` | **Fixed in the fork** (`ResolveTransverterWorkspace`). |
| B2 | Browsers on `https://app.zeussdr.com` and a Zeus staging site are allowed to call the engine (CORS, credentials allowed) | `StationEngineEndpoints.cs:20-25, 107`, `NativeWrapperCorsPolicy.cs:18`, `StationEngine/Program.cs:27` | A page on those sites can drive our engine on localhost, transmit included. Proposal: remove. |
| B3 | SPE Expert / Taurus amplifier control only works with a Zeus Link entitlement | `StationEngine/ProductFeatureState.cs`, `SpeTaurus/ExpertAmpServerTunePreflight.cs:42` | Always off for us. Proposal: let SimpleHPSDR turn it on, if we want SPE support. |
| B4 | "Native G2" display and audio-input tuning only when the Zeus Link G2 package is installed | `DisplayPerformanceOptions.cs:92` (`/usr/share/zeus-link-g2/package-kind`), `MiniAudioInput.cs:49` | SimpleHPSDR on a G2 Ultra never gets it. Proposal: detect the G2 itself, or a variable SimpleHPSDR sets. |
| B5 | Zeus Link product settings store, `--product-lan-https-port`, LAN proxy, app-control endpoints for `zeus-link-launcher` | `ProductBundleSettingsStore.cs`, `StationEngine/Program.cs:376`, `EngineAppControlEndpoints.cs` | Unused by us. Leave (harmless) or remove. |
| B6 | Cache janitor for launcher-managed engine caches and Zeus single-file bundles | `EngineCacheJanitor.cs`, `BundleExtractionJanitor.cs` | Runs at start, but only acts on folders with Zeus's channel marker; did nothing here. Leave, or remove. |
| B7 | Plugin registry (`Zeus-SDR/zeus-community-features`), plugin host, VST host | `Zeus.Plugins.Host/Registry/HttpRegistryClient.cs:19` and the rest of `Zeus.Plugins.Host` | Already pointed at an empty folder with VST off. Proposal: leave it out of our build. |

## C. Outside services (only when a client asks)

| # | What | Where |
|---|------|-------|
| C1 | God's Eye map feeds (USGS, OpenSky, ADS-B, NASA FIRMS, TomTom and others), user agent `ZeusSDR/1.0` | `Zeus.Server.Hosting/GodsEyeFeedsService.cs`, `GodsEyeHosting.cs` |
| C2 | Satellite elements from CelesTrak | `Zeus.Server.Hosting/SatelliteTrackingService.cs:137` |
| C3 | KiwiSDR directory | `Kiwi/KiwiDirectoryService.cs:61` |
| C4 | POTA / SOTA / DX Summit spots | `Zeus.Contracts/Dtos.cs:2231` |

SimpleHPSDR uses none of them. Leave, or remove with the Zeus web-app features in a later clean-up.

## D. Names only

`ZEUS_*` environment variables, `.zeusdb` export names, `Zeus.*` namespaces. Leave:
renaming them only makes rebasing on each Zeus release harder.
