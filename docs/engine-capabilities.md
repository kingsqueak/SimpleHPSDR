# What the engine can do, and what SimpleHPSDR uses

An inventory of our Station Engine fork (`external/station-engine`, 2.0.68-simplehpsdr.4),
made 2026-11-08 to decide what to keep. The engine is about 185,000 lines of C# plus the
native WDSP, FFTW and codec libraries. It answers 311 HTTP routes; SimpleHPSDR calls 129
of them, plus the display, audio and meter streams.

Each entry has a suggestion. Nothing has been removed yet. Sizes are rounded line counts
of the C# that implements each feature, to show weight, not exact boundaries.

- **Keep:** SimpleHPSDR depends on it.
- **Could use:** working engine features the app doesn't offer yet. Keep them if you'd
  like the feature some day.
- **Remove:** serves only Zeus's web app or Zeus Link, or isn't wanted. Removing makes
  the engine smaller and starts fewer things, but every removed piece is a patch to carry
  through each Zeus rebase. Leaving it switched off is the cheaper option where it costs
  nothing at run time.

"Starts with the engine" means it runs as a background service from start-up, used or not.

## 1. The core: keep

| Feature | What it does | Size (lines) |
|---|---|---|
| Radio protocols | OpenHPSDR Protocol 1 and 2: discovery, connect, take over, IQ and audio streams | 19,000 |
| DSP pipeline (WDSP) | Receivers, demodulation, filters, AGC, noise blankers and reduction (NR1-4, NNR), notches, squelch, the panadapter and waterfall | 23,500 |
| Radio and TX services | Frequencies, modes, slices, split, RIT/XIT, MOX, TUNE, drive, TX timeout, SWR protection, the band-plan transmit guard | 13,000 |
| Transmit audio | Mic capture, leveler, compressor, CESSB, CFC, EQ, phase rotator, ALC, noise gate, monitor, TX profiles | 6,000 |
| PureSignal | Calibration, automatic feedback attenuation, saved corrections | 2,000 |
| PA | Gains per band, calibration, power and SWR meters | 3,000 |
| Audio | Native output and mic on Mac, Linux and Windows, ASIO, the radio's speaker and line-in | 5,500 |
| Bands | Band plans, band memory and band stack, transverters, antennas, the band outputs (open collector), HL2 IO board | 3,500 |
| Calibration | S-meter, frequency (WWV), overload protection | 1,000 |
| CAT | Kenwood TS-2000 with the Thetis ZZ commands, over the network and serial ports | 2,500 |
| TCI | For WSJT-X, JTDX, loggers, SimpleDream's DRM and DeepCW | 5,000 |
| CW | Keyer, sidetone, the engine's CW decoder (the "Engine" choice in the CW window), CW ID | 8,500 |
| FM | CTCSS, DCS, repeater shift, tone burst | 500 |
| PTT | Radio PTT input, serial foot switch, external PTT | 1,500 |
| Settings and backup | The settings database, profiles, backup and restore | 3,500 |
| Logging and diagnostics | `engine.log`, the startup log, diagnostics routes | 1,500 |
| Windows firewall | Checks and grants the rule ("SimpleHPSDR (HPSDR receive)") | 1,000 |

## 2. Working features the app doesn't offer: could use

| Feature | What it does | Size | Starts with the engine | Suggestion |
|---|---|---|---|---|
| FreeDV / RADE | Digital voice: FreeDV and RADE modems | 2,000 | No | **Could use.** A FreeDV mode would be a real feature for some users. Remove if you never want it. |
| Virtual audio cable input | Takes transmit audio from a virtual cable (VB-Cable, BlackHole) while keyed, for digital modes without TCI | 400 | Yes, idle | **Could use** for programs without TCI. |
| WSPR decoder | Decodes WSPR in the background | 1,600 | **Yes, always** | **Remove**, or offer it. It runs whether or not anyone looks. |
| IMD meter | Measures third- and fifth-order products of a two-tone signal | 300 | No | **Could use**: a natural readout beside 2-TONE and PureSignal. |
| Antenna analyser (VNA) | SWR sweeps with a NanoVNA, the HL2 or the radio itself | 2,000 | No | **Could use** for antenna work. Remove if not. |
| SPE Expert amplifier control | Drives SPE Expert/Taurus amplifiers (band, power, tune); locked behind a Zeus Link licence today | 5,600 | No | Keep and unlock **if** a user has an SPE amp; otherwise remove. |
| HF-AUTO controller | Reads an HF-AUTO antenna tuner/switch controller's network output | 500 | **Yes** | **Remove** unless someone has one. |
| Wideband signal markers | Finds signals across the span and marks their centre and width | 1,600 | No | **Could use** later (click a marker to tune). |
| Station favourites | Five tuning memories kept in the engine | 250 | No | Leave; the app has its own memories. |
| Contest log | A simple contest log in the engine | 800 | No | **Remove**: loggers do this better, through CAT/TCI. |
| FT8/FT4 settings | Stores settings for a digital-mode window the engine doesn't have | 300 | No | **Remove** (settings only, no decoder). |
| Roger beep | A beep at the end of each over | small | No | Leave (tiny). |
| Extra receivers for others | Virtual and guest receivers, used by Public Listening below | 1,250 | No | Remove with Public Listening. |

## 3. Zeus's web app and Zeus Link only: remove

| Feature | What it does | Size | Starts with the engine | Notes |
|---|---|---|---|---|
| Web-app screen settings | Theme, toolbar, clock, hot keys, panel layout, workspaces, onboarding, operator UI settings for Zeus's browser screens | 2,400 | No | Our transverter fix no longer needs workspaces. |
| Zeus Link product hooks | Product settings store, LAN proxy, app control for Zeus's launcher, product plugin audio, out-of-process modem lease, cache and bundle janitors | 6,500 | Janitor at start | Section B5-B6 of `engine-zeus-dependencies.md`. |
| Outside DSP and TX engines | Loads separately distributed DSP and "Protocol 3" transmit engines | 1,000 | No | Closed add-ons for Zeus; not ours to ship. |
| Plugin and VST host | Zeus community plugins, VST and AU effects | 9,100 | Plugin manager starts | Already pointed at an empty folder with VST off. |
| Public Listening | Shares your receiver and spectrum with listeners over the internet | 3,400 | No | A big feature with privacy and bandwidth questions. **Remove** unless wanted. |
| TDOA direction finding | Joins KiwiSDR stations to locate a transmitter, with a "friends" relay | 2,200 | No | **Remove.** |
| KiwiSDR client | Listens to remote KiwiSDR receivers and reads the KiwiSDR directory | 2,900 | **Yes** | Contacts kiwisdr.com only when asked. **Remove.** |
| God's Eye map | Aircraft, ships, earthquakes, fires and cameras from outside feeds | 2,000 | No | **Remove.** |
| Satellites | Satellite pass prediction from CelesTrak | 3,500 | No | **Remove** (or keep for future satellite work). |
| APRS map | APRS-IS position tracking | 500 | No | **Remove.** |
| Spots | POTA, SOTA and DX Summit spot lists | small | No | **Remove**, or use later for a spots panel. |

## 4. Remove for safety

| Feature | What it does | Notes |
|---|---|---|
| ~~Hidden QRM generator (`/api/tx/qrm`, `/api/tx/qrm/text`)~~ **Removed 2026-11-08** | Feeds made-up interference (tones, drift, pulses) or text drawn onto the waterfall into the transmit audio. It doesn't key by itself, but goes out on the air with any MOX or PTT, and the text route can key. | Starts with the engine. Transmitting deliberate interference is illegal on amateur bands, and one stray request to localhost would arm it. **Remove first.** 600 lines, one small patch. |

## Suggested order

1. ~~Remove the QRM generator.~~ Done 2026-11-08 (fork, not yet committed).
2. **Stop what starts and isn't used.** The WSPR decoder, the KiwiSDR service and HF-AUTO all
   start with the engine. Not starting them is a few lines and saves CPU on a Raspberry Pi.
3. **Decide the "could use" list.** FreeDV, the IMD meter, the virtual audio cable input
   and the antenna analyser are the ones worth building into the app.
4. **Remove the Zeus web-app and Zeus Link code in a few large patches**, grouped as in
   section 3, so each Zeus rebase has a handful of patches to carry, not dozens.
   Plain deletions of whole folders rebase easily; edits inside files Zeus keeps changing,
   such as `RadioService.cs` and `DspPipelineService.cs`, are the hard part.

## How big the rebase cost is

Each Zeus release so far changed mostly the core (radio, DSP, TX). The features in
section 3 live in their own files and folders, so deleting them seldom conflicts. The
cost is in the few places that register them at start-up
(`StationEngineHostingExtensions.cs`, `StationEngine/Program.cs`) and in the routes
mapped there. Keeping those registration edits together in one patch keeps rebases short.
