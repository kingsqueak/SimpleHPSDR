# Brick SDR support

Brick SDRs (hardware by Linoobs, gateware by Sergey EU1SW) are Hermes-class
(Brick2) and Angelia-class (Brick3) OpenHPSDR radios, popular in Europe. They
announce themselves as a plain Hermes or Angelia; SimpleHPSDR's engine (fork
tag v2.0.68-simplehpsdr.11) recognises them by the MAC address their firmware
sets, the same way deskHPSDR does:

| MAC | Radio |
|---|---|
| 02:B2:01:xx:xx:xx | Brick2 14-bit LP |
| 02:B2:02:xx:xx:xx | Brick2 16-bit LP |
| 02:B2:03:xx:xx:xx | Brick2 14-bit HP |
| 02:B2:other | Brick2 |
| 02:B3:xx:xx:xx:xx | Brick3 |

Tested by a Brick owner with the Windows build on 2026-11-09: working.
The two Protocol 2 items marked unconfirmed below still want a specific report.

## What the engine does for a Brick

Brick2 (Protocol 1 or 2):
- Classified as Hermes whatever board byte the firmware reports (board 0x06
  with a newer code version would otherwise read as a Hermes-Lite 2).
- One antenna: no Ant 2/3 or RX aux selections offered.
- Power meter scale 15 W. Drive keeps the Hermes 100 W gain table, so 100 %
  drive is full output (the radio limits itself).
- PA always enabled (transmit safety and per-band PA-off still apply).
- Protocol 2: the 48 kHz IQ level correction (+29 dB hot firmware) and the
  single-ADC DDC0 receiver layout, from the Zeus team's earlier work.
- Protocol 2, transmitting cross-band (TX needs a different low-pass filter
  from RX1, e.g. split onto slice B on another band): DDC0's frequency word
  follows the transmit frequency while keyed. deskHPSDR reports no RF
  otherwise. **Unconfirmed on hardware.**

Brick3 (Protocol 2, Angelia class):
- In receive, the RX1/RX2 frequencies are mirrored into the DDC0/DDC1 words,
  which its STM32 reads to pick the band and filters. **Unconfirmed on
  hardware.**

## For testers

Receive first, then transmit into a dummy load only.

1. The radio picker shows "Brick2 14-bit LP" (or your variant) and Protocol 1/2.
2. Receive on several bands: S-meter and panadapter sensible at 48, 96, 192
   and 384 kHz spans (48 kHz used to read 29 dB hot).
3. No antenna choices are offered in Ant / Config > Antennas.
4. TUNE into a dummy load at low drive: power reading sensible, scale 15 W.
5. Brick2 on Protocol 2: split TX onto slice B on another band (RX 40 m, TX
   20 m) and TUNE briefly into the dummy load: is there RF on the TX band?
6. Brick3: change bands in receive and listen for the filter relays; does the
   band filter follow the dial?
7. If the radio stops answering after a while (some networks lose the Brick's
   ARP entry), note it; deskHPSDR works around this with a static ARP entry.

Report the radio, firmware version, protocol and what you saw.
