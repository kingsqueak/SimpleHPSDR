# SimpleHPSDR on the ANAN G2's own Pi

The G2 has a Raspberry Pi compute module inside (a CM4 in the original G2, a
CM5 in the newer one) with its own screen. SimpleHPSDR can run there, beside
Saturn's **p2app**, the G2's program that talks to the radio's FPGA (over
XDMA) and serves it on the network. SimpleHPSDR connects to p2app just as it
would from another computer, and the front panel's knobs and buttons reach it
through p2app too.

One SimpleHPSDR package (arm64) covers both the CM4 and CM5, on Raspberry Pi OS
Bookworm or Trixie (64-bit). The front panel works on both the original G2
panel (p2app's GPIO v1 and GPIO v2 builds) and the G2V2 / G2 Ultra panel,
because p2app reads the panel and passes it on.

## Install

On the G2's Pi, download the `arm64` .deb from Releases, then:

```sh
cd ~/Downloads
sudo apt update
sudo apt install ./simplehpsdr_*_arm64.deb
```

(`uname -m` should say `aarch64`. A 32-bit system, `armv7l`, isn't supported yet;
please report it.)

## Run p2app with the front panel

p2app must be running, with its panel option `-p`. The G2's own autostart for
it (from the Saturn repository) is:

```sh
cp ~/github/Saturn/autostart-files/g2-autostart-p2app.desktop ~/.config/autostart/
```

Turn off piHPSDR's autostart if it's there (`~/.config/autostart/g2-autostart-piHPSDR.desktop`),
since only one program can use the radio.

## Start SimpleHPSDR at login

```sh
mkdir -p ~/.config/autostart
cat > ~/.config/autostart/simplehpsdr.desktop <<'END'
[Desktop Entry]
Type=Application
Name=SimpleHPSDR
Exec=sh -c "sleep 8; simplehpsdr"
END
```

The pause gives p2app time to start first. On the G2's 1024x600 screen
SimpleHPSDR opens in its touch layout, full screen.

## First connect

Pick the G2 in the radio list. If it isn't listed, type `127.0.0.1` (this Pi)
in the address box and choose ANAN-G2 (or G2-1K) as the model. SimpleHPSDR
reconnects to it at the next start.

## The front panel

Config > Hardware > G2 FRONT PANEL shows whether p2app has connected and which
panel it found. The VFO knob tunes; the other knobs and buttons start with
piHPSDR's layout and can each be given another job there. The ATU button is
handled by p2app itself.

To quit SimpleHPSDR on the G2's screen: ☰, then Quit at the top of the drawer,
then tap Quit again within 4 seconds. On the original G2 panel, holding FCN quits too; on any panel,
"Quit SimpleHPSDR (hold)" can be given to a button in Config (it needs a long
press, so a stray tap can't close the radio).

SimpleHPSDR listens on port 19090 for p2app (changeable there) and tells the
radio that port only after it's listening, and sets it back to 0 before it
stops, as p2app needs.

## For testers

Please report the G2 (CM4 or CM5), `cat /etc/os-release`, the panel (original
or G2V2/Ultra), whether the radio was found by itself or by 127.0.0.1, which
controls work, and CPU use on the G2's screen (especially on a CM4).
