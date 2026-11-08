# ui-shots

Renders SimpleHPSDR's windows and flyouts off-screen (Avalonia headless, Skia)
and writes PNGs plus `audit.md`: each window's controls under 40 px and
anything clipped, and a few probes (gestures, flyout scrolling, layout choice).
It never starts the engine or touches a radio: the main window is shown with
a fake connected state.

Run with a throwaway home so your real settings are never read or written (the
tool refuses to start otherwise):

    export CFFIXED_USER_HOME=$(mktemp -d)   # macOS: .NET finds the app data folder through this
    export HOME=$CFFIXED_USER_HOME            # Linux, and dotnet's own caches
    dotnet run --project tools/ui-shots -- /tmp/shots-before

Compare two runs (the desktop layout must not change):

    for f in /tmp/shots-before/*.png; do magick compare -metric AE "$f" "/tmp/shots-after/$(basename "$f")" null:; echo " $(basename "$f")"; done

The station guide differs by a few pixels between runs (its clock).
`--touch` / `--desktop` / `SIMPLEHPSDR_LAYOUT` work as in the app.
