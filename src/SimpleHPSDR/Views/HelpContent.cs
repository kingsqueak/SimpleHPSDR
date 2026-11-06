namespace SimpleHPSDR.Views;

/// <summary>
/// The Help window's text. In a topic's body, "## " starts a subheading,
/// "- " a bullet, and a blank line a new paragraph.
/// </summary>
public static class HelpContent
{
    public static readonly (string Title, string Body)[] Topics =
    [
        ("Getting started", """
SimpleHPSDR runs an OpenHPSDR radio from your computer. The radio connects over your network; the app shows the band and holds the controls, and an engine inside the app does the signal processing.

## First start
The very first start takes around 8 minutes or more while the signal processing is tuned to your computer. A window shows progress. Leave the app open until it finishes. This only happens again after some updates.

## Band plan
Choose the plan for your region or licence class. You can change it later in Config > Station.

## Choosing your radio
The radio picker opens next. Pick your radio and press Connect. Several radios report the same ID, so choose yours under Radio model if asked: it sets the S-meter calibration and the power amplifier and PureSignal defaults. Tick "Connect to this radio automatically next time" to skip this.

If no radio appears, check that it's on and on the same network, or type its address under "Or by address". Network help lists what a firewall has to allow.

## Finding your way around
- Top row: radio, Config, Span, antenna, frequency and meters.
- Second row: band, mode, filters, ANF, NB, NR, and a close-up of the signal.
- Middle: the panadapter and waterfall.
- Bottom: display controls, then transmit and receive controls, and the clock.

## The most useful tip
Most controls have more behind a right-click. Hover over anything for a short explanation. Blue means a control is on.
"""),

        ("Shortwave listening", """
Choose SWL from the Mode menu. The radio defaults to SAM, with a 10 kHz broadcast filter if SAM has no saved settings on that band, and the station guide opens. SAM is only the starting point: change the mode or filter as you like and SWL stays on.

While SWL is on, an SWL button appears beside Mode. Click it to open the guide; you can close the guide at any time and SWL carries on. Right-click the button to turn SWL off.

The guide lists non-digital stations from the EiBi shortwave schedule (eibispace.de), from long wave up, in your local time. It updates itself and works from its saved copy when you're offline. Click a heading to sort, pick a language, or search. Click a station to tune it; your mode doesn't change.

While SWL is on, a box at the bottom of the waterfall shows the station EiBi lists on your tuned frequency, within 1 kHz: its name, language, target area, site and end time. Any others sharing the frequency are named under it. This works however you tune, not only from the guide, and the box goes away when nothing is listed there.

The box and the guide show what's scheduled, not what's identified from the signal, so what you're hearing may be a different station. Voice utility stations in the list usually need USB rather than SAM.
"""),

        ("Coming from a traditional radio", """
Nearly everything on a front panel is here under a different name. This is where to find the controls you know.

- VFO knob: the mouse wheel over the frequency or the panadapter. Step sets the size of each click.
- Keypad frequency entry: click the frequency and type.
- Band scope: the panadapter. The waterfall below it is the same picture over time.
- AF gain: the AF slider.
- RF gain: the AGC-T slider, which sets where AGC starts working.
- Preamp and attenuator: RF.
- IF width or filter: the filter buttons, or drag the edges in the close-up.
- Noise blanker: NB. Noise reduction: NR. Notch: ANF, with manual notches on its right-click.
- Second VFO or sub-receiver: slice B.
- Split: put slice B on the transmit frequency and transmit on it.
- RIT and XIT: the same names, beside the frequency.
- RF power: DRIVE, in the TX popup.
- Mic gain: MIC, in the TX popup.
- Speech processor: Config > TX Audio.
- Monitor: Mon.
- Antenna switch: Ant.
- Menu system: Config.

## What's different
You can see the band instead of tuning across it. A weak signal shows as a line in the waterfall before you can hear it, so you can click straight to it.

The filters are also far steeper than crystal filters, and you can set their edges to any width.
"""),

        ("Tuning", """
## Ways to tune
- Wheel: over the frequency or the panadapter, each click moves one tuning step. A sideways scroller works too, more slowly.
- Click: click a signal on the panadapter to go to it.
- Type: click the frequency, type it and press Enter. Esc cancels.
- Band button: choose a band.

## Tuning step
The highlighted digit is the one your step moves. Use Step beside the frequency to change it. 1 kHz or 500 Hz suits SSB; 10 Hz or 1 Hz is fine tuning.

## Lock
The padlock stops tuning from here, so you can't bump the dial. Software using CAT or TCI can still tune it.

## Moving the display
Drag the panadapter to slide the band under the display. With CTUN on, a drag that reaches the end of the span pauses for a moment, then the tuned frequency moves to the middle of the display so you can keep going.

CTUN changes what a click does. With it on, the display stays still and only the receive frequency moves, so you can tune across a section of band without the picture sliding.

## Seeing more or less of the band
- Span sets the full width the radio takes in, from its sample rate. A wider span shows more of the band and uses more computer power.
- Zoom spreads part of that span across the display. Click it to go between full span and zoomed. Right-click to set the zoomed span.
"""),

        ("Mini window", """
The MINI button to the right of the meters swaps the main window for a small one you can keep in a corner of the screen. Drag its edges to any size and everything scales to fit.

- The frequency tunes with the wheel, or click it to type one.
- Below it are the signal, power, SWR and ALC meters.
- MUTE turns red while the audio is muted.
- TX is green and turns red while transmitting. It only transmits while you hold it down, and it unkeys when you let go or click away.

The small arrows at the top right, or closing the mini window, bring the main window back. If MOX or TUNE is already on, TX does nothing.
"""),

        ("Reading the display", """
## Panadapter
The upper graph shows signal strength across the band, with frequency left to right and level up the side. The flat grassy line at the bottom is the noise floor. Anything standing above it is a signal or interference.

## Waterfall
The lower part shows the same thing over time, with new rows arriving at the top. Brighter colours are stronger. SSB looks like wandering streaks, CW like dashes, and AM like a bright line with sidebands either side. Steady vertical lines are usually carriers or birdies.

## On the display
- The red line is slice A's frequency; slice B has its own colour.
- The shaded band is your receive filter: what you can hear.
- The yellow line is the AGC threshold.
- A red band is a manual notch.

## Close-up
The small box at the top right shows 6 kHz from the dial as audio frequency, with the filter shaded. It's the best place to see how a voice fits in the filter.

## Adjusting the picture
- Auto keeps the display range on the noise floor. It's on by default and needs no attention.
- Display sets the range by hand, the resolution, the waterfall speed and POP.
- POP makes the waterfall follow the noise floor, which lifts weak signals out of it.
- The dotted bar between the panadapter and waterfall drags to change their sizes.
"""),

        ("Bands and modes", """
## Bands
Click the band button and choose. Each band remembers where you were. Each band and mode together remember settings such as the filter, AGC, drive and noise tools, which come back when you return to that mode. Changing mode never moves the dial.

Right-click the band button to save the current frequency, or recall one you saved. It works like a set of memories for that band.

## Modes
Click the mode button to choose.
- LSB: voice below 10 MHz, by amateur custom.
- USB: voice above 10 MHz, and most digital modes.
- CWL and CWU: CW, listening below or above the carrier. See the CW topic.
- AM: full-carrier broadcast and amateur AM.
- SAM: synchronous AM. It locks to the carrier, which holds up better when a signal fades.
- FM: narrow FM, usually 10 m and up.
- DIGL and DIGU: for digital modes. DIGU is the usual choice for FT8 and similar. Audio passes straight through in both directions: transmit EQ, compressor, CESSB, CFC, leveler, noise gate and phase rotator stand aside, as do receive noise reduction, blankers, notches and squelch. Their buttons keep your settings for other modes.

## AM and FM options
Right-click the mode button in AM, SAM or FM. AM has broadcast-style transmit settings. FM has narrow deviation, CTCSS tones, reverse and the 1750 Hz tone burst.
"""),

        ("CW", """
Choose CWL or CWU with the mode button, and a WPM button appears beside ANF.

## Your paddle or key
Connect it to the radio. Right-click WPM to choose a straight key or iambic A or B, and to set the speed, weight, sidetone and paddle direction. The wheel over the WPM button changes speed at any time.

Break-in means keying transmits by itself, and the radio drops back to receive after the hang time. Lower hang times let you hear between words.

## Sending from the keyboard
Click WPM to open the CW window, which stays open beside the main window. Type and press Enter, or press a macro. Both transmit straight away, so have your antenna or dummy load connected. Stop or Esc ends it at once and drops anything queued, and so does turning MOX off. Edit macros in the same window.

## Keying from the keyboard
No key handy, or just practising? The keyboard can act as a straight key or a paddle. It's for slow sending and practice, as keyboard timing isn't as steady as a real keyer.

To set it up:
1. Turn on the TCI server in Config > CAT & TCI. The keyboard keys the radio through it.
2. Choose CWU or CWL, with Break-in on (right-click WPM).
3. Click WPM to open the CW window, then tick Keyboard keying. It's off each time the app starts.

Then, with the CW window in front and the cursor not in a text box:
- Space is a straight key: the tone follows your finger.
- The left arrow sends dits and the right arrow sends dahs, at your WPM. Hold one to repeat it, or both to alternate. Reverse paddles swaps them.
- Esc stops everything.

These transmit straight away, so have your antenna or dummy load connected.

Transmit stays on between elements so the relays don't click on each one. For the arrows that's the Hang time (right-click WPM), and for the spacebar it's at least 700 ms, or twice Hang time if that's longer.

Your mic is muted while this happens and comes back after transmit drops, so your voice can't go out between elements. If the app stops mid-way, the mic is put right at the next connect.

For safety, a key can't stay down for more than 3 seconds, it lifts if you click away from the CW window, and keyboard keying won't start while MOX or TUNE is on.

## Reading CW
Press Decode in the CW window to show received CW as text. It works best on a clean, steady signal with a narrow filter, and it won't match a good ear on a weak or hand-sent signal.

## Tuning and filters
In CW the frequency shown is the carrier of the signal, and the filter is centred on the dial. Click a signal on the panadapter, or tune until it lines up with the dial line, and you'll hear it at a 600 Hz tone.

The close-up at the top right is your tuning indicator. It spans 3 kHz in CW, and its dial line sits at the 600 Hz CW offset, not at 0. Tune so the signal's peak sits on that line, in the middle of the shaded filter, and you're on frequency.

Use a narrow filter, 200 to 500 Hz. The receive tone is fixed at 600 Hz, so leave the sidetone there if you want to match signals by ear.
"""),

        ("Filters", """
A filter sets how wide a slice of the band you hear. Narrow it to remove a neighbour, and widen it for better audio.

## Choosing one
Click a filter button. Each mode has its own set, and the highlighted button is the one in use.

## Setting the edges
Drag the handles in the close-up at the top right. In USB, LSB and CW there are two: the low and high cuts. In AM, SAM and FM there's one, and both sides move together.

As a guide: 2.4 to 2.8 kHz for SSB voice, 200 to 500 Hz for CW, 6 to 10 kHz for AM.

## Your own filters
Right-click a filter button to edit its width and label, then Save. Reset to default puts it back.

## Filter shape
Config > Filters sets how steep the filters are. Steeper edges add delay and use more computer power, so leave this alone unless you have a reason.
"""),

        ("Receiving", """
## Volume
- AF sets the overall level. Its A and B flags set each slice's own level on top of that.
- Mute silences the receiver.
- SQL is the squelch. Click for on or off, and right-click for the level. Adaptive follows the band noise, so you don't have to reset it.
- RX Lev brings weak stations up to a steady listening level.

## RF: preamp and attenuator
Click RF to open the front end. Each click steps through preamp, 0 dB and 10 to 30 dB of attenuation. Auto ATT adds attenuation by itself if the receiver overloads, and is best left on. On a noisy band, attenuation can improve what you hear: if the noise drops and the signal doesn't, keep it.

## AGC
AGC keeps the audio level steady as signals change. On the AGC button, blue is automatic. Grey is manual, where you choose the speed: slow for steady voice, fast for quick changes such as CW.

AGC-T is the threshold, much like RF gain on an analogue radio. Raise it until the yellow line sits just above the noise. Moving it turns automatic AGC off.

## Noise tools
- NB is the noise blanker, for sharp pulses such as ignition or power-line noise. It works best on strong impulses, and too much can distort strong nearby signals.
- NR is noise reduction, for steady hiss. NR1 to NR4 and NNR are different methods, so try each. A little goes a long way: too much sounds watery.
- ANF removes steady tones automatically, such as carriers and tuner-uppers.

NB and NR have settings on a right-click.

## Manual notches
ANF hunts for tones by itself, and a manual notch stays exactly where you put it, which takes a steady tone out more deeply. Right-click ANF, press Add on panadapter, then click the tone. The panel sets each notch's width (try 50 Hz for one carrier), and Remove or Clear all takes them away.
"""),

        ("Two receivers and split", """
SimpleHPSDR has two independent receivers, called slices. It's like a second VFO that you can hear at the same time.

## Adding slice B
Right-click the panadapter and add slice B there, on the same panadapter or in a new one. The same menu makes a slice active, moves slice B, mutes either slice, and removes slice B.

## Which one you're controlling
The active slice is the one the frequency, mode and filter controls act on. Click a slice's tag on the panadapter to make it active. A⇄B swaps the two frequencies.

## Working split
1. Tune slice A to the DX station.
2. Add slice B on the frequency where they're listening.
3. Right-click the panadapter and choose Transmit on slice B.

A marker beside the frequency shows that transmit is on the other slice.

## RIT and XIT
These move receive (RIT) or transmit (XIT) a little without moving the dial. Click to turn on or off, use the wheel for 10 Hz a click, and right-click to clear. RIT works on slice A only.

## DUP
DUP keeps you hearing the receivers while transmitting. It needs a separate receive antenna or a slice on another band, or the radio's own signal will overload them.
"""),

        ("Transmitting", """
Before you transmit, make sure an antenna or dummy load is connected to the antenna the band uses.

## Keying
- MOX transmits until you click it again.
- TUNE sends a steady carrier at the tune level, for antenna tuners.
- Your microphone's PTT or a foot switch works too, once it's set in Config > Station.

## The TX button
The TX button in the bottom row opens three levels:
- DRIVE sets output power. It's your RF power control.
- TUNE sets the power used by the TUNE button. Keep it low.

Each slider shows a percentage of its full travel and the power in watts. With a maximum drive set in Config > Amp, the top of the slider is that maximum.
- MIC sets microphone gain, with a level meter.

Set MIC by speaking normally and watching the meters. Aim for steady ALC movement rather than maximum. If your audio sounds harsh, turn it down.

## Meters while transmitting
- PWR is output power, from the radio's own meter. That meter can read low, so check it against an external one. Config > PA can correct it.
- SWR is how well the antenna matches. Stop and check if it's high.
- ALC shows the transmit audio being held back to stop overdriving.

## Hearing yourself
Mon steps through:
- Mon: your mic through the transmit chain, without transmitting. Ideal for setting up audio.
- TX Mon: hearing yourself while transmitting.
- Mon+TX: both.

Use headphones. Right-click for the level.

## Protection
Config > Amp has a transmit timeout that drops transmit if you stay keyed too long, and a maximum drive that sets the top of the DRIVE and TUNE sliders.
"""),

        ("Transmit audio", """
Config > TX Audio holds everything that shapes your voice, in the order it's processed. All of it is optional.

## Profiles
Save your settings under a name and load them later: one for DX, one for ragchewing. Default is built in and can't be overwritten. A profile holds only what's on this TX Audio page, and it applies in every mode, so make a separate profile if you want different settings for AM, say. CW and FreeDV don't use the TX filter. PureSignal settings aren't part of a profile, so the internal or external coupler stays as set in Config on every band and mode.

## TX filter
Sets how wide your signal is. "TX filter follows the RX filter" ties it to your receive filter. Otherwise set it by hand. 2.8 kHz is a usual SSB width.

## Equalizer
Shapes the tone of your voice. The graphic view has ten sliders. Tick Parametric for six bands instead: drag a numbered point for frequency and gain, and use the wheel over it for Q. Bands 1 and 6 can be a pass filter or a shelf. The dashed line is what the radio actually does; very narrow bands can come out a little shallower.

## Leveler
Keeps your level steady as you move around the mic. It's the first thing to turn on.

## CFC
A compressor working across the whole audio range at once. Compression in dB is the gain given to quiet sounds, and everything is held to the same ceiling, so more dB means more compression. Post gain then shapes the result.

CFC adds delay: the FFT size sets how much. If you raise it, keep the PTT release delay long enough, or the end of each transmission gets cut off.

## Other tools
- Speech processor: Compressor and CESSB add talk power. Use them lightly.
- Noise gate: quiets the mic between words.
- Phase rotator: evens out the peaks in a voice, so it's easier to process.
"""),

        ("PureSignal", """
Every amplifier distorts a little, which splatters onto nearby frequencies. PureSignal samples your transmitted signal, compares it with what it should be, and corrects for the distortion, for a much cleaner signal.

## Setting it up
1. Set the source in Config > PureSignal.
2. Set the drive level you'll be using.
3. Turn on PS.
4. Switch to a dummy load, right-click PS and press PS CAL. It keys the radio in short two-tone bursts. Run it again until PS turns green.

That's it until you change the drive level.

## The PS button
- Green: on and locked.
- Yellow: on but not locked.
- Grey: off.

## Also on the right-click
- 2-TONE transmits a test signal.
- Save correction keeps what PureSignal learned, and Restore brings it back without calibrating.
- The status box shows the feedback level (FB), which is best around 128 to 181.
"""),

        ("Clock and ID timer", """
The clock at the bottom right shows your local time and UTC.

## ID timer
Click ID above the clock to turn on a reminder at each even ten minutes on the clock, while connected to a radio.
- Grey at 00:00: off.
- White: counting down.
- Green: from 5 seconds before the mark to 5 seconds after.

If you're transmitting at the mark, it waits and plays when you unkey.

It plays on this computer's speakers and never goes out over the air. Config > Station has the sound, your own .wav, the volume and a Test button.
"""),

        ("Your station", """
Config holds settings you set once. Changes take effect as you make them, and Undo changes puts every page back to how it was when the window opened.

## Antennas
Ant in the top row picks this band's transmit and receive antennas. Config > Antennas shows every band at once.

## Radio, PA and calibration
- Radio: your radio model and the light or dark theme.
- PA: rated power, each band's PA gain, automatic calibration and correction from an external meter.
- Calibration: S-meter trim, frequency correction using WWV, and overload protection.

## Setting the power output
Output is Rated power × the DRIVE or TUNE percentage, with each band's PA gain setting how hard the radio is driven to get it. A higher PA gain means less drive for the same setting. Leave Rated power at your radio's real figure. To limit power, use Maximum drive in Config > Amp: at 100 W rated, 25% tops out at 25 W.

The stock gains are a sound starting point. Reset to stock on the PA page puts them back at any time, and also clears any calibration and PWR correction.

## Calibrating with the radio's meter
Config > PA > Calibrate evens out the bands automatically, using the radio's own power meter.
1. Connect a dummy load rated for 50 W carriers to the transmit antenna port of every band.
2. Turn off any external amplifier.
3. Press Calibrate, tick both confirmations and press Start calibration.

The radio keys a carrier at 10, 25 and 50 W on each HF band in turn. Stop ends it at any time and puts the settings back. If only some bands pass, you choose whether to keep those; the others are left as they were.

It needs a band plan with every HF band including 60 m, set in Config > Station. If it says the band plan has no legal frequency, choose your licence class or country there.

It uses the radio's own power meter, so the bands end up even with each other, but that meter can be several dB out, which can leave real power well above what's shown. Check with an external meter afterwards. Run PS CAL again too, since PureSignal is turned off for the run.

On some radios the meter reads several dB low, and calibrating then pushes real power well above what's shown. On those, skip Calibrate and use the stock gains with the external meter steps below.

## Correcting power with an external meter
If you have a meter you trust, this puts real output and the PWR display right.
1. Key TUNE into a dummy load, at 10 W for example.
2. Note what TUNE is set to, what PWR reads, and what your meter reads, then unkey.
3. Enter them under External meter on the PA page and press Apply.

Apply shifts every HF band by the gap between what you asked for and what your meter read, and corrects the PWR display by the gap between PWR and your meter. Then check at a higher power. Run PS CAL again afterwards, since drive has changed. Applying a shift replaces any calibration on those bands with a single gain.

## Audio
Config > Audio chooses your speakers and microphone: a computer mic, or the radio's mic jack with its boost and bias. On Windows, ASIO can be used with an audio interface.

If your radio has a speaker or headphone jack, choose Radio speaker jack as the speaker output to hear receive audio there instead of on the computer. Changing to or from it restarts the engine and reconnects, which takes a few seconds. Mon can't play while it's chosen.

## External amplifier
Config > Amp sets the delays that protect its relays. Pre-key delay holds back RF until the relays close, and Config > Band Outputs switches its bands through the radio's open-collector pins.

## Also in Config > Station
- Band plan: the band edges for your region or licence class. It's used for PA calibration; SimpleHPSDR doesn't block transmit by it.
- PTT: the radio's PTT input, or a foot switch on a serial port. Both can be turned off.
- Diversity: combines two receive antennas.
- Transverters: for VHF and up.
"""),

        ("Other software", """
Logging, digital and contest programs can control the radio through Config > CAT & TCI.

- TCI suits WSJT-X, JTDX, loggers and skimmers.
- CAT is the older method that most logging programs use, over the network or a serial port.

Turn on the server you need and note the port. Changes here need Restart engine to apply; the radio reconnects in a few seconds.

"This computer only" keeps TCI private. Choose Network only if another computer needs it. The running TCI status shows at the bottom right, beside CPU.
"""),

        ("Troubleshooting", """
## No radio found
Check the radio is on and on this network, and try its address under "Or by address". The radio picker's Network help button lists what a firewall must allow. On Windows, answer yes when asked about StationEngine, unless Zeus is on the same computer: Network help explains that case. On a Mac, allow the Local Network prompt.

## "Getting ready for first use" won't finish
It's slow, not stuck: around 8 minutes on a fast computer and longer on an older one. If you quit, it starts again next time.

## The radio engine stopped
A window offers Restart engine, which reconnects with transmit off. It's worth keeping engine.log and engine.previous.log from the app's data folder for a bug report.

## Audio or mic problems
Check Config > Audio. If you hear crackling on ASIO, use a larger buffer.

## Weak or distorted receive
Check RF: too much preamp near strong signals overloads the receiver. Try attenuation, and leave Auto ATT on.

## Power doesn't match another meter
PWR is the radio's own meter and can read low, and everything set from it inherits that. Compare against a meter you trust, and use External meter on the PA page. If you ran Calibrate and power is higher than shown, press Reset to stock first. Keep drive low until it's sorted out, especially with an amplifier connected.

## End of transmission cut off
Raise the PTT release delay in Config > TX Audio, especially with CFC at a large FFT size.

## PS won't go green
Check the source in Config > PureSignal, and watch the status on the PS right-click. "Stalled" means the HW peak setting needs adjusting; Adjust HW peak automatically can do it for you.

## Firmware
Click the radio's name, then Firmware. A failed flash can leave the radio needing recovery, so use only the file made for your exact radio, on a wired network, and don't turn anything off until it finishes.
"""),
    ];
}
