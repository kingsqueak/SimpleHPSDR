# Start the packaged Windows build on a CI machine: no radio or sound hardware,
# but it proves the engine and its native libraries load on real Windows, and
# leaves a screenshot of the app. Usage: smoke-windows.ps1 <folder> <screenshot>
param([string]$Dir, [string]$Shot)
$ErrorActionPreference = 'Stop'

$port = 58123
$env:ZEUS_PREFS_PATH = "$env:RUNNER_TEMP\smoke.db"
$engine = Start-Process -FilePath "$Dir\engine\StationEngine.exe" -WorkingDirectory "$Dir\engine" `
    -ArgumentList '--port', $port, '--native-audio-output', 'true' -PassThru -NoNewWindow `
    -RedirectStandardOutput engine.out -RedirectStandardError engine.err
$ok = $false
for ($i = 0; $i -lt 60 -and -not $engine.HasExited; $i++) {
    try { Invoke-RestMethod "http://127.0.0.1:$port/api/station/version" -TimeoutSec 2 | ConvertTo-Json; $ok = $true; break }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $ok) { Get-Content engine.out, engine.err -ErrorAction SilentlyContinue; throw 'engine did not answer' }
# Reading the devices loads miniaudio and zeus_asio.
$audio = Invoke-RestMethod "http://127.0.0.1:$port/api/audio/devices"
$audio | Select-Object supported, backend, availableBackends, error, asioError | ConvertTo-Json
Write-Host "asio drivers: $(@($audio.asioDrivers).Count)"
if ($audio.error -match 'DLL|load|module') { throw "native audio failed: $($audio.error)" }
Stop-Process -Id $engine.Id -Force
Start-Sleep -Seconds 2

$app = Start-Process -FilePath "$Dir\SimpleHPSDR.exe" -WorkingDirectory $Dir -PassThru
Start-Sleep -Seconds 25
if ($app.HasExited) { throw "app exited with code $($app.ExitCode)" }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save($Shot)
Get-Content "$env:LOCALAPPDATA\SimpleHPSDR\engine.log" -Tail 15 -ErrorAction SilentlyContinue
Stop-Process -Id $app.Id -Force
Get-Process StationEngine -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'smoke test passed'
