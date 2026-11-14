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
Choose the plan for your region or licence class, or General Coverage for no restrictions. You can change it later in Config > Station, where you also choose whether going outside the plan warns you or blocks transmit.

The strip along the top of the spectrum follows the plan: green where it lets you transmit, red where it keeps the band for another licence class, and grey for broadcast bands, CB and WWV. White lines mark the edges. Turn it off, or make its colours fainter, in Config > Station > Band plan.

## Choosing your radio
The radio picker opens next. Pick your radio and press Connect. Several radios report the same ID, so choose yours under Radio model if asked: it sets the S-meter calibration and the power amplifier and PureSignal defaults. Tick "Connect to this radio automatically next time" to skip this.

Brick SDRs (Brick2 and Brick3) are recognised and named in the list.

If no radio appears, check that it's on and on the same network, or type its address under "Or by address". Network help lists what a firewall has to allow.

## Finding your way around
- Top row: radio, Config, Span, antenna, frequency and meters.
- Second row: band, mode, filters, ANF, NB, NR, and a close-up of the signal.
- Middle: the band strip, the panadapter and the waterfall.
- Bottom: display controls, then transmit and receive controls, and the clock.
- The Tools menu has the antenna analyser and the wideband view; the Help menu has this window and update checks.

## The most useful tip
Most controls have more behind a right-click. Hover over anything for a short explanation. Blue means a control is on.
"""),

        ("Shortwave listening", """
## DRM
Where the DRM decoder is installed, the Mode menu has DRM. It needs the TCI server on, in Config > CAT & TCI. Choosing it opens a list of DRM broadcasts: click one to tune it, or tune by hand. DRM stations are listed by the centre of their channel. CTUN turns off while DRM is on and comes back afterwards.

While DRM is on, a DRM button appears beside Mode, and a box at the bottom of the waterfall shows the station, how the signal is doing and any text message it sends. Mute and AF work as usual. Click the button to open the list again. Right-click it to pick another service on stations that carry more than one, or to turn DRM off. DRM is receive only.

## The station guide (SWL)
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
- Preamp and attenuator: RFG.
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

## Memories
Mem, left of the frequency, opens 16 memories, the same on every band. Click one to tune to its frequency and mode. Right-click one (long-press on a touch screen) to store the frequency and mode you're on; that replaces what was there. An empty one says Open. When the popup closes they sort by frequency, lowest first, left to right.

## Moving the display
Drag the panadapter to slide the band under the display. With CTUN on, a drag that reaches the end of the span pauses for a moment, then the tuned frequency moves to the middle of the display so you can keep going.

CTUN changes what a click does. With it on, the display stays still and only the receive frequency moves, so you can tune across a section of band without the picture sliding.

## Seeing more or less of the band
- Span sets the full width the radio takes in, from its sample rate. A wider span shows more of the band and uses more computer power.
- Zoom spreads part of that span across the display. Click it to go between full span and zoomed. Right-click to set the span, from full at the left end of the slider, and Centre, which sets where the dial sits across the display (left, centre or right). Centre works with CTUN on or off. Ctrl+wheel over the spectrum (⌘+wheel on a Mac) or a trackpad pinch zooms too; the dial stays where it is while you zoom.
"""),

        ("Mini window", """
The MINI button to the right of the meters swaps the main window for a small one you can keep in a corner of the screen. Drag its edges to any size and everything scales to fit.

- The frequency tunes with the wheel, or click it to type one.
- Below it are the signal, power, SWR and ALC meters.
- MUTE turns red while the audio is muted.
- TX is green and turns red while transmitting. It only transmits while you hold it down, and it unkeys when you let go or click away.

The small arrows at the top right, or closing the mini window, bring the main window back. If MOX or TUNE is already on, TX does nothing.
"""),

        ("Touch screens and small screens", """
On a small screen, such as a radio's 1024 by 600 front panel, SimpleHPSDR shows a layout with large buttons instead of the desktop one. Config > Radio > Layout chooses: Automatic uses it on screens narrower than 1080, or pick Desktop or Touch. Everything in it works with a mouse too.

## On screen
- Top: the drawer (☰), band, mode, the frequency, the meter, MOX and TUNE.
- Bottom: volume (AF), drive (DRV), Mute, filter, NB, NR, step and RIT.
- ☰ opens a drawer with everything else, in four tabs: Receive, Transmit, Slices and Station. Station has Config, the station guide, the CW window, the antenna analyser and Full screen. Quit is at the top of the drawer, beside ✕: tap it, then tap again within 4 seconds.

## Tuning
- Drag the frequency sideways: each 20 pixels is one step. The wheel works too.
- Tap the frequency for a keypad: type the number, then press MHz or kHz.
- Tap the spectrum to tune; drag it to slide the band. Let go while still moving and it glides on and slows down.
- Pinch the spectrum to zoom (two fingers, or a Mac trackpad), use − and + in its corner, or Ctrl+wheel (⌘+wheel on a Mac).
- Lock beside the frequency stops tuning from the screen. Dial position in the drawer's Receive tab sets where the dial sits across the spectrum.

## Long press
Holding a finger still for half a second does what a right-click does: on Band it opens the band stack, on the spectrum the slice menu, on most buttons their settings. With a mouse, holding the button down on a button does the same.

## Other windows
While the touch layout is on, Config, the station guide and the other windows open with the same large controls, and on a small screen they fill it.
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
The small box at the top right shows 6 kHz from the dial as audio frequency (3 kHz in CW), with the receive filter shaded. It's the best place to see how a voice fits in the filter.
- Drag the filter's edges in it to set the low and high cuts. The box along its top shows them in Hz.
- The area under the trace is coloured like the waterfall, so the strongest part of the audio stands out.

Right-click it (long press on a touch screen) for its options. They're remembered.
- Fill: Waterfall colours (the default) or Plain, a single shade under the trace.
- High resolution (on by default): the engine works out a fine spectrum just for the close-up, instead of cutting it from the panadapter. You see the separate tones in a voice, a CW signal as a narrow spike, and carriers and hums sharply. Off, it uses the panadapter's coarser picture, the same as before.
- Detail (with High resolution): 4, 2 (the default) or 1 Hz. Finer detail looks back over a longer stretch, about a second at 1 Hz, so it reacts more slowly. 4 Hz follows speech best; 1 Hz is for spotting a carrier or hum exactly.
- Averaging: 0.05 to 3 seconds. Short (the default) follows every syllable; a second or more shows where the signal's energy usually sits, which helps when setting the filter edges. Reset puts it back to the default.

If the engine can't give the high-resolution picture (an older engine, or it stops coming), the close-up quietly falls back to the panadapter's and tries again a minute later.

## Adjusting the picture
- Auto keeps the display range on the noise floor. It's on by default and needs no attention.
- Display sets the range by hand, the resolution, the waterfall speed and POP.
- POP makes the waterfall follow the noise floor, which lifts weak signals out of it.
- Config > Colors changes the spectrum line, the shading under it, the background (plain or fading) and the waterfall colours. Custom… beside each colour picks any colour you like. "Back to the original colours" undoes it all.
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
Press Decode in the CW window to show the active slice's CW as text. The box beside it picks the decoder. Engine is the radio engine's own: it prints each character at once and does well on a clean, steady signal with a narrow filter. DeepCW is a neural-network decoder that copies weak, fading and hand-sent CW far better, but prints about 2 to 4 seconds behind. DeepCW takes its audio from the TCI server, so turn that on in Config > CAT & TCI first. It isn't available on Intel Macs. With Words ticked, word spaces the decoder missed are put back (CQSSK1XMK1XM reads CQ SS K1XM K1XM); a run is only split when every piece is a known CW word, callsign, report or number.

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
Drag the handles in the close-up at the top right. In USB, LSB and CW there are two: the low and high cuts. In AM, SAM and FM there's one, and both sides move together. The box along the top of the close-up shows the low and high cuts in Hz as you drag. Right-click the close-up (long press on a touch screen) for its colours, high resolution, detail and averaging; "Close-up" under Reading the display describes them. A long averaging time shows where a voice's energy usually sits, which makes the edges easy to place.

As a guide: 2.4 to 2.8 kHz for SSB voice, 200 to 500 Hz for CW, 6 to 10 kHz for AM.

## Your own filters
Right-click a filter button to edit its width and label, then Save. Reset to default puts it back.

## Filter shape
Config > Filters sets how steep the filters are, as a number of filter taps: Soft is 1,000, Normal 2,000 and Sharp 4,000, and the list goes up from there. More taps give steeper edges but add delay and use more computer power. Minimum phase keeps the delay down at high tap counts. The TX shape is one of the two biggest transmit delays, and Config shows the estimate as you change it.
"""),

        ("Receiving", """
## Volume
- AF sets the overall level. With slice B on, it becomes two sliders, A over B, each setting that slice's own level on top of the overall level, which stays where you left it.
- Mute silences the receiver.
- REC and the voice keyer: see Recording and the voice keyer.
- SQL is the squelch. Click for on or off, and right-click for the level. Adaptive follows the band noise, so you don't have to reset it.
- RX Lev brings weak stations up to a steady listening level. Right-click it for its settings. Auto uses the engine's own values. Custom lets you set the target level, the most boost it may add, how fast it comes up (attack), how fast it backs down (release), and how long it holds before backing down (hang).

## Receive EQ
The AF button beside the volume slider is the receive EQ. Click it for on or off (blue = on). Right-click it (long press on touch) for ten sliders from 60 Hz to 5 kHz, ±20 dB, that shape what you hear on both slices: cut the low end to make a weak SSB signal clearer, or lift the treble on a muffled station. Flat puts them all back to 0 dB. The EQ stays out of DIGU and DIGL so decoding programs get plain audio, and it's remembered between sessions.

## RFG: preamp and attenuator
Click RFG to open the front end. Each click steps through preamp, 0 dB and 10 to 30 dB of attenuation. Auto ATT adds attenuation by itself if the receiver overloads, and is best left on. On a noisy band, attenuation can improve what you hear: if the noise drops and the signal doesn't, keep it.

## AGC
AGC keeps the audio level steady as signals change. On the AGC button, blue is automatic. Grey is manual, where you choose the speed: slow for steady voice, fast for quick changes such as CW. Right-click AGC for automatic AGC's setting: how far above the band's noise it sets the threshold. 20 dB is usual; lower brings weak signals up, along with the noise.

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
Right-click the panadapter and add slice B there, on the same panadapter or in a new one. The same menu makes a slice active, moves slice B, mutes either slice, and removes slice B. With slice B in its own panadapter, the menu also puts the two panadapters side by side or one above the other.

## Which one you're controlling
The active slice is the one the frequency, mode and filter controls act on. Click a slice's tag on the panadapter to make it active.

Left of the frequency, A and B show which is which: blue is the active slice, red is the one that transmits, and grey is the other. Click A or B there to transmit on that slice. "Swap A and B" in the panadapter's right-click menu swaps their frequencies.

## Working split
1. Tune slice A to the DX station.
2. Add slice B on the frequency where they're listening.
3. Right-click the panadapter and choose Transmit on slice B.

A marker beside the frequency shows that transmit is on the other slice.

## RIT and XIT
These move receive (RIT) or transmit (XIT) a little without moving the dial. Click to turn on or off, use the wheel for 10 Hz a click, and right-click to clear. RIT works on slice A only.

## Slice B on the Rx2 jack
Both slices normally hear the main antenna. Radios with two receivers inside (ANAN-7000DLE, 8000DLE, G2 and similar) also have an Rx2 jack on the back. To have slice B listen on an antenna plugged in there, such as a receive-only antenna, open Ant and set B to Rx2 jack. The Ant button then lights and shows B:Rx2 jack. With nothing in the jack, slice B goes quiet. While slice B is the active slice, ATT and Pre set the Rx2 jack's own attenuator (ATT shows Rx2), and Pre steps 0, 10, 20, 30 dB: the preamp belongs to the main antenna. Auto ATT looks after both inputs.

## DUP
DUP keeps you hearing the receivers while transmitting. It needs a separate receive antenna or a slice on another band, or the radio's own signal will overload them.
"""),

        ("Transmitting", """
Before you transmit, make sure an antenna or dummy load is connected to the antenna the band uses.

## Keying
- MOX transmits until you click it again.
- TUNE sends a steady carrier at the tune level, for antenna tuners.
- VOX transmits when you speak. Right-click it to set the threshold against your mic level, and the delay after you stop. It works in voice modes, turns off when you disconnect, and is off each time the app starts. Use headphones: there's no anti-VOX, so sound from speakers can key the radio. Mon pauses while VOX is on unless you use headphone mode (see "VOX and Mon" below).
- Your microphone's PTT or a foot switch works too, once it's set in Config > Station.

## When transmit stops by itself
The engine protects the radio, and a red banner in the main and mini windows says why it stopped you. It stays until you click Dismiss or transmit again.
- SWR too high: SWR stayed above the limit for half a second. Check the antenna and tuner before transmitting again. The engine always stops MOX at 2.5:1, and TUNE at 6:1 above 35 W. You can set a lower limit for MOX, and an amber warning before it, in Config > Amp.
- TX timeout: you were keyed longer than the limit in Config > Amp. An amber warning appears shortly before, while you can still unkey. It goes when you do.
- Out of band: with Block transmit set in Config > Station, your band plan doesn't allow the frequency or mode, so transmit stopped or never started. With Warn only, an amber banner tells you instead and you stay on the air. General Coverage does neither.
- External fault: hardware connected to the radio reported a problem.

VOX turns itself off after any of these, so it can't key straight back into the same fault. PS CAL, queued CW and two-tone stop too. If you use a paddle, it stays off until you transmit once some other way, such as MOX.

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
- ALC shows the transmit audio being held back to stop overdriving. Its bar runs to 20 dB: green is fine, orange from 10 dB, red from 15 dB means turn the mic gain down. On FM it shows deviation (DEV) instead, red when over full deviation.

## Hearing yourself
Mon steps through:
- Mon: your mic through the transmit chain, without transmitting. Ideal for setting up audio.
- TX Mon: hearing yourself while transmitting.
- Mon+TX: both.

Use headphones. Right-click for the level.

## VOX and Mon
While VOX is on, Mon is paused: its button goes dark but keeps your setting, and the status bar says so. Turning VOX off brings Mon back as you had it.

This is because VOX listens to your mic, and Mon plays your voice. Through speakers, even quiet ones, the mic hears Mon. VOX takes that for you still talking: it keys the radio on its own, and with TX Mon the transmit audio keeps VOX open, so the radio stays keyed sending that feedback until you unkey it. Pausing Mon while VOX is on means VOX only ever hears you. To hear yourself, turn VOX off and use MOX or your PTT.

Headphones must be used to monitor during VOX. With headphones on, right-click VOX and tick Headphone mode: Mon and TX Mon then keep working while VOX is on. Leave it off if you ever use speakers.

## VOX delay
The VOX delay is how long transmit stays on after you stop speaking. A short delay gets you back to receive sooner, which suits a fast-moving group. A long one keeps transmit on through the pauses between phrases. It starts at 300 ms. If VOX drops in the middle of your sentences, lengthen it. Going much below 300 ms gains little, because your last word is still on its way out of the transmit processing.

When VOX lets go after a pause there is little or nothing left to send, so the radio returns to receive quickly. The RX resume delay (Config > Amp) is separate. It only keeps receive audio muted for a moment after any unkey, so you don't hear the switch back.

## Protection
Config > Amp has a transmit timeout that drops transmit if you stay keyed too long, and a maximum drive that sets the top of the DRIVE and TUNE sliders.
"""),

        ("Transmit audio", """
Config > TX Audio holds everything that shapes your voice, in the order it's processed. All of it is optional.

## Profiles
Save your settings under a name and load them later: one for DX, one for ragchewing. Default is built in and can't be overwritten. A profile holds only what's on this TX Audio page, and it applies in every mode, so make a separate profile if you want different settings for AM, say. CW and FreeDV don't use the TX filter. PureSignal settings aren't part of a profile, so the internal or external coupler stays as set in Config on every band and mode.

## Sharing a profile
Export… saves the chosen profile to a .shtx file you can send to another SimpleHPSDR user, by email or a group, say. They press Import…, pick the file, and the profile is added to their list and loaded at once. Their own profiles are kept: if the name is taken, the imported one gets a number. A profile holds only TX audio settings, nothing about your radio, its power or its calibration, so it's safe to share. Export writes the profile as last saved, so press Save first if you've changed something.

## TX filter
Sets how wide your signal is. "TX filter follows the RX filter" ties it to your receive filter. In AM, SAM and DSB the receive filter covers both sidebands, so your audio goes up to half its width: a 10 kHz AM filter sends audio up to 5 kHz. In AM and SAM the low cut still applies either way, as the modulator's high-pass: to change it, untick follows, set the low cut and save your profile. Otherwise set both by hand: the high cut goes up to 10 kHz (8 kHz of audio in FM). 2.8 kHz is a usual SSB width; wider is for AM and wide-band SSB, where the band and your licence allow it.

## Equalizer
Shapes the tone of your voice. The graphic view has ten sliders. Tick Parametric for six bands instead: drag a numbered point for frequency and gain, and use the wheel over it for Q. Bands 1 and 6 can be a pass filter or a shelf. The dashed line is what the radio actually does; very narrow bands can come out a little shallower.

## Leveler
Keeps your level steady as you move around the mic. It's the first thing to turn on.

## CFC
A compressor working across the whole audio range at once. Compression in dB is the gain given to quiet sounds, and everything is held to the same ceiling, so more dB means more compression. Post gain then shapes the result.

CFC adds delay: the FFT size sets how much, from about 16 ms at 2048 to about 166 ms at 16384. Config > TX Audio shows the total delay as you change it.

## Other tools
- Speech processor: Compressor and CESSB add talk power. Use them lightly.
- Noise gate: quiets the mic between words.
- Phase rotator: evens out the peaks in a voice, so it's easier to process.
"""),

        ("PureSignal", """
Every amplifier distorts a little, which splatters onto nearby frequencies. PureSignal samples your transmitted signal, compares it with what it should be, and corrects for the distortion, for a much cleaner signal.

## Setting it up
The first time you turn on PS, a guided setup opens. It asks where the sample comes from, reminds you to connect a dummy load and set your usual power, then calibrates. You can run it again any time from the PS right-click or Config > PureSignal.

To recalibrate by hand: connect a dummy load, right-click PS and press PS CAL. It keys the radio in short two-tone bursts until PS turns green.

After that you rarely need PS CAL again. While you transmit, PureSignal keeps adjusting the correction from your own speech, about once a second, so it follows changes in power and band by itself. The PS right-click shows when it last did.

## Remembered for each band
"Remember the correction for each band" (PS right-click) is on to start with. After an over that updated the correction, it is saved for the band you are on. Next time you connect, or come back to that band, PureSignal starts from the saved correction, so your first words are already clean, and it carries on adjusting from there.

Run PS CAL again, or Reset calibration in Config > PureSignal, after you change amplifier or how the sample is taken: the saved corrections belong to the old setup. Reset also forgets them.

## In CW
PureSignal isn't used in CW. A CW signal is sent straight to the radio and doesn't pass through the correction, and a single keyed tone has no splatter of the kind PureSignal removes. While you send CW it stands still: it keeps the correction it has, changes nothing, and picks up where it left off on your next voice transmission. The PS panel says "Not used in CW" and the PS button is yellow. 2-TONE and PS CAL work in any mode.

## The PS button
- Green: on and correcting your signal.
- Yellow: on but not correcting yet. Transmit for a few seconds, or run PS CAL.
- Grey: off.

## Also on the right-click
- 2-TONE transmits a test signal.
- IMD: while 2-TONE transmits, the PS panel shows how far the 3rd- and 5th-order intermodulation products sit below the tones (IMD3, IMD5, in dBc; more negative is cleaner). With PureSignal on it measures your amplifier's real output (the PS sample) and starts PureSignal from a reset: leave 2-TONE on about 15 seconds and the panel shows the uncorrected and corrected IMD3 and the difference. 2-TONE zooms in to measure and puts your zoom back when it stops. PS CAL doesn't measure IMD: its bursts are too short, and PureSignal is still learning during them. Use 2-TONE from its button after PS CAL to see the result. If the panel says to zoom in, the tones are too close together on screen to measure; the 2-TONE button zooms for you.
- "Correction updated" shows the last time PureSignal adjusted the correction, or that it started from the one saved for this band.
- "Remember the correction for each band": see above.
- The status box shows the feedback level (FB), which is best around 128 to 181.
- A line in plain words says what PureSignal is doing and what to do if it's stuck.
- "Signal peak" and "HW peak" should be close: your signal's peak just under HW peak, at 94 to 100% of it. PS CAL sets this for you.
"""),

        ("Recording and the voice keyer", """
## Recording
REC, right of TX, opens a small tape deck over the bottom of the waterfall. Click REC again to put it away; it stays open while you use the rest of the radio.
- RX / MIC picks what ● records: RX is what you hear on the active slice after the receive filter; MIC is your microphone, for voice keyer messages such as your CQ or call.
- ● starts and stops recording (red while recording). The counter shows the time.
- The name box shows the recording ▶ plays: the newest unless you choose another from ▾. Type in the box to rename it. ▾ lists them all; ✕ deletes one (click twice).

## Playing back
▶ plays the chosen recording through your speakers; receive is muted while it plays. ■ stops it.

Turn on TX (it goes red) and ▶ transmits the recording instead, at the TX level set by the spinner (dB). It keys like MOX, with all the usual checks, and MOX or ■ ends it. A recording of the receiver goes out as heard, through the TX filter only; a microphone recording goes through your mic gain and transmit processing like live speech. At 0 dB a recording matches your own voice level on the ALC.

Right-click REC for the full list: rename, delete, listen, transmit and Open folder. Recordings are WAV files in Music/SimpleHPSDR.

## The voice keyer
The four grey buttons right of REC each transmit one recording. Right-click one (long press on touch) to give it a label and choose its recording. Click it to transmit, at the recorder's TX level; it's red while on the air, and a second click or MOX stops it. A button with nothing assigned is dimmed.

Keys: ⌘1 to ⌘4 on a Mac, Ctrl+1 to Ctrl+4 on Windows and Linux, or F5 to F8 (on a Mac hold fn, as F5 is dictation).

On the touch layout REC is beside Mute: tap it to record, long press for the recordings. To have the voice keyer there too, tick "Voice keyer buttons on the touch layout" in REC's long-press panel: four buttons appear at the right of the waterfall, above the bottom row.
"""),

        ("FreeDV digital voice", """
FreeDV is digital voice in an SSB-width channel. SimpleHPSDR uses RADE, FreeDV's newest mode: it decodes received speech in place of the usual audio, and turns your voice into a RADE signal when you transmit. The older modes (700D, 700E, 1600) aren't included.

## Getting started
1. Set your callsign in Config > Station. RADE sends it at the end of each transmission, so other stations see who you are.
2. Tune to a FreeDV frequency (14.236 MHz is a common one) and choose FreeDV in the Mode menu.
3. Talk normally at your usual mic gain, and watch the box on the waterfall.

FreeDV uses LSB below 10 MHz and USB above, and USB on 60 m, as other FreeDV stations do. SimpleHPSDR follows this by itself.

## The FreeDV box
On the waterfall it shows whether a RADE signal is in sync, its SNR, and the callsign the other station sends at the end of their over. While you transmit it shows your mic level as a bar: green is good, red means too loud, so turn the mic gain down.

## Settings
Right-click the Mode button while it shows FreeDV (long press on touch):
- Squelch: keeps quiet when no RADE signal is in sync, instead of playing warbling noise. Hold sets how long it stays open through a brief loss of sync. Min SNR, if set, also keeps it closed below that signal-to-noise ratio.
- TX level: how hard the RADE signal drives the transmitter. Lower it if ALC shows: RADE needs a clean signal.
- Mic gate: silences the room between your words, so background noise doesn't become garble at the far end. Opens at and Hold set when it opens and how long it stays open.
- Level my voice (mic AGC) and Mic tone (EQ): optional shaping before the encoder.
- Back to defaults restores the engine's own values.
"""),

        ("Antenna analyser", """
Tools > Antenna Analyser (touch: ☰ > Station > Analyser) sweeps SWR across a band or range and draws it.

## Measuring with your radio
"This radio" uses the radio's own SWR bridge. It transmits a low-power carrier, about 5 W, stepping across the range, so it asks before it starts:
- Put an amplifier on standby or bypass first.
- It transmits through the antenna selected for that band. To test the analyser itself, sweep a dummy load: it should show a flat line near 1.0.
- Every frequency is checked against your band plan before it keys; a range outside your licence is refused.
- If your radio gives too little power at the low setting, the analyser raises it a little by itself until the bridge can read it, and puts your TUNE power back afterwards.
- The radio goes back to your frequency when the sweep ends. Stop ends a sweep at once.
A Hermes-Lite 2 uses its built-in analyser instead.

## Measuring with a NanoVNA
Plug the NanoVNA in by USB, choose NanoVNA, pick its serial port and press Connect. It measures without transmitting, and also shows impedance (R + jX). Pick only the NanoVNA's port: your radio's CAT or an amplifier's port could be upset by it.

## Reading the result
- The graph shows SWR on a log scale, with lines at 1.5, 2 (dashed), 3 and 5. The band is shaded and the lowest point is marked.
- Move the pointer over the graph to read the frequency and SWR at any point (and R + jX from a NanoVNA).
- Beside it: the lowest SWR and where, return loss, the bandwidth under 1.5, 2 and 3, and impedance and Q when the source measures them.
- Through a manual tuner, the dip shows the tuner's setting: SWR rises as you move away from where it was tuned.

## Saved sweeps and calibration
Every sweep is saved in the list on the right: click one to show it again, or delete it. Calibration (open, short, load, thru) appears only for sources that need it.
"""),

        ("Wideband view", """
Tools > Wideband View (touch: ☰ > Station > Wideband) opens a window showing 0 to 60 MHz at once, straight from the radio's receiver. It only looks: your frequency, mode and display carry on as they are, and the radio stops sending the wideband data a few seconds after you close the window. It needs a Protocol 2 radio.

## What it shows
- A trace on top and a waterfall below, with the amateur bands shaded and named. The red line is where your receiver is tuned.
- The level scale follows the signals by itself.
- Move the pointer over it to read the frequency and level.
- While you transmit the picture holds still.

## What it's good for
- Hunting noise: arcing or a failing insulator raises the floor everywhere, a solar inverter or switching supply makes a comb of evenly spaced lines, and a single carrier is one line. Switch something off and watch.
- Overload: it shows what reaches the receiver after its filters, such as AM broadcast stations or a strong local, when the overload light comes on.
- Your antenna and tuner: the noise floor rises where they're tuned and falls away either side.
- Band activity at a glance: busy FT8 spots, a contest, 10 m opening, or which shortwave broadcast bands are alive.

Each point covers several kHz, so it shows where signals are, not the signals themselves; weak signals near the noise don't show.
"""),

        ("ANAN G2 on its own Pi", """
The ANAN G2 has a Raspberry Pi inside (a CM4 in the original G2, a CM5 in the newer one) with its own screen. SimpleHPSDR can run there, beside the G2's p2app, which talks to the radio's hardware. It installs from the same arm64 package on Raspberry Pi OS Bookworm or Trixie. The full steps are in the docs (g2-pi.md) on GitHub.

## Setting it up
- p2app must be running, with its panel option (p2app -p). The G2's own autostart can start it; turn off piHPSDR's autostart if it's there, as only one program can use the radio.
- SimpleHPSDR can start at login a few seconds after p2app, and opens in its touch layout, full screen.
- Pick the G2 in the radio list; if it isn't listed, type 127.0.0.1 as the address and choose ANAN-G2 or G2-1K.

## The front panel
The G2's knobs and buttons work through p2app: the original G2 panel (on GPIO v1 or v2) and the G2V2 / G2 Ultra panel. The VFO knob tunes; the other controls start with piHPSDR's layout. Config > Hardware > G2 FRONT PANEL shows whether p2app has connected and which panel it found, and lets you give any knob or button another job.

## Quitting
Tap Quit at the top of the ☰ drawer, then tap it again within 4 seconds. On the original G2 panel, holding FCN quits too; on any panel, "Quit SimpleHPSDR (hold)" can be given to a button in Config.
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

## CW ID
Right-click ID and choose Send my callsign in CW automatically (in the touch layout: CW ID in the drawer). Your callsign then goes out in Morse as a tone mixed into your voice, at least every 10 minutes of operating. It needs your callsign in Config > Station.
- The box reads CW ID and counts down to the next ID.
- next over (green): an ID is owed and goes out when you next talk.
- sending (green): it's on the air now. If you unkey mid-ID, it finishes first.

It never keys the radio by itself: it only rides on your own SSB, AM or FM transmissions, not CW or digital modes. Turning it off sends one last ID on your next over. Config > Station sets its speed (20 WPM at most), tone, level and interval.
"""),

        ("Your station", """
Config holds settings you set once. Changes take effect as you make them, and Undo changes puts every page back to how it was when the window opened.

## Antennas
Ant in the top row picks this band's transmit and receive antennas. Config > Antennas shows every band at once.

## Radio, PA and calibration
- Radio: your radio model, the light or dark theme, and the layout (desktop, or large buttons for touch screens).
- Colors: the spectrum and waterfall colours.
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
Config > Amp sets the delays that protect its relays. Pre-key delay holds back RF until the relays close, and Config > Outputs switches its bands through the radio's outputs.

RX resume delay keeps receive audio quiet for a moment after you unkey, so you don't hear the switch back. It starts at 33 ms. If you hear a click or thump from a relay-switched amplifier on each unkey, raise it a little. It doesn't protect the amplifier, so a longer setting only delays hearing the other station.

## Options for particular radios
Config > Hardware shows only the options your radio has. On other pages, a jack, bias, antenna relay or receive input your radio doesn't have stays on the page, greyed out.
- Dither and randomizer: can lower unwanted signals made inside the radio. For the ANAN-G2, G2-1K, 7000DLE and 8000DLE, and most radios on Protocol 1.
- Hermes-Lite 2: band volts for an amp, the HL2+ codec, and the four user outputs.
- HL2 IO board (N2ADR): when one is found, its inputs and any fault, an RX and TX antenna for each band (1 is the board's usual antenna; 2 and up only where your board has more wired), and its antenna tuner: Tune (sends a carrier while the tuner asks for it, 15 seconds at most), Bypass and Stop.
- ANAN G2 front panel: see ANAN G2 on its own Pi.
- Anvelina-PRO3: the extra outputs 7 to 10 for each band.

## Palstar HF-AUTO tuner
Config > Antennas shows the tuner's state from W1TR's HF-Auto program. In HF-Auto, choose TCI for the radio and point it at SimpleHPSDR's TCI (Config > CAT & TCI), and turn on its UDP status output to the port shown. Tick "Show the HF-AUTO tuner's status"; if HF-Auto runs on another computer, give its address and press Apply. The page then shows the mode, frequency, antenna, L and C, power, SWR, and whether it's tuned. HF-Auto still does the tuning.

## Also in Config > Station
- Your callsign: sent by FreeDV at the end of each transmission. Type it and press Enter or Save.
- Band plan: the band edges for your region or licence class, or General Coverage for no restrictions. Outside the plan it warns you or blocks transmit, as you choose. PA calibration also uses it; in General Coverage that's the last plan you chose.
- Backup and restore: Back up saves all your settings to one file: radio settings, band memories, PA calibration, filters, Mem memories, colours and TX profiles. Restore replaces your settings with a backup's and restarts SimpleHPSDR. PureSignal's calibration isn't included; it learns again within seconds.
- PTT: the radio's PTT input, or a foot switch on a serial port. Both can be turned off.
- Diversity: combines two receive antennas.
- Transverters: for VHF and up. Each one you turn on gets its own band button, and the frequency shows the band you're really on.
"""),

        ("Other software", """
Logging, digital and contest programs can control the radio through Config > CAT & TCI.

- TCI suits WSJT-X, JTDX, loggers and skimmers. Programs see the radio as SimpleHPSDR.
- CAT is the older method that most logging programs use, over the network or a serial port. It acts as a Kenwood TS-2000, so choose TS-2000 as the radio in the other program.

Turn on the server you need and note the port. Network changes need Apply now; the radio reconnects in a few seconds.

"This computer only" keeps TCI private. Choose Network only if another computer needs it. The running TCI status shows at the bottom right, beside CPU.
"""),

        ("Troubleshooting", """
## No radio found
Check the radio is on and on this network, and try its address under "Or by address". The radio picker's Network help button lists what a firewall must allow. On Windows, answer yes when asked about StationEngine. On a Mac, allow the Local Network prompt.

## "Getting ready for first use" won't finish
It's slow, not stuck: around 8 minutes on a fast computer and longer on an older one. If you quit, it starts again next time.

## The radio engine stopped
A window offers Restart engine, which reconnects with transmit off. It's worth keeping engine.log and engine.previous.log from the app's data folder for a bug report.

## Audio or mic problems
Check Config > Audio. If you hear crackling on ASIO, use a larger buffer.

## ASIO won't stay selected
If a driver fails, the app goes back to System audio and the status line on the Audio page says why. Most failures come from one of these:
- Another program has the interface. Many ASIO drivers let only one program use them at a time. Close DAWs, other radio programs and anything else using the interface, then choose ASIO again.
- Windows is using the interface too. If it's the Windows default playback or recording device, some drivers refuse to start. In Windows Sound settings, make another device the default, then choose ASIO again.
- The interface is unplugged, switched off or asleep. Reconnect it and choose ASIO again.
- The driver is 32-bit only, or was left behind when the interface was removed. The status says so. Install the maker's current 64-bit driver, or remove the old one.
- The driver can't run at 48 kHz, which the radio needs. If its own settings lock another rate, open Driver panel and change it to 48 kHz.
If ASIO is selected but you hear nothing or the radio gets no mic, the channels are probably wrong. Speaker output and Computer mic list every output and input the interface has. Pick the ones your speakers and mic are actually plugged into.

ASIO4ALL and other generic drivers work, but use the interface's own driver when it has one.

When the status says "Could not instantiate the selected ASIO driver", it adds a Windows check with an error code. Include that whole line in a bug report.

## Weak or distorted receive
Check RFG: too much preamp near strong signals overloads the receiver. Try attenuation, and leave Auto ATT on.

## Power doesn't match another meter
PWR is the radio's own meter and can read low, and everything set from it inherits that. Compare against a meter you trust, and use External meter on the PA page. If you ran Calibrate and power is higher than shown, press Reset to stock first. Keep drive low until it's sorted out, especially with an amplifier connected.

## End of transmission cut off
Raise the PTT release delay in Config > TX Audio. The engine already holds transmit for the speech still in the chain, and this adds a short extra wait.

## PS won't go green
Run the guided setup from the PS right-click. It starts from your radio's default HW peak, which clears a setting left over from earlier tries. If it still won't lock, the line on the PS right-click says why: the sample too strong or too weak, or the wrong feedback source.

## Firmware
Click the radio's name, then Firmware. A failed flash can leave the radio needing recovery, so use only the file made for your exact radio, on a wired network, and don't turn anything off until it finishes.
"""),
    ];
}
