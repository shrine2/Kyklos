# Installs the Kyklos build you downloaded. Unpack the whole zip first, then run either
# Install.cmd (double-click) or:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Install.ps1
#
# Everything is per-user: the exe lands in %LOCALAPPDATA%\Programs\Kyklos, the login entry and
# the "Convert with Kyklos" Send To shortcut point there, and no administrator rights are needed.
# Only downloading files this way earns SmartScreen's "Windows protected your PC" page - choose
# More info, then Run anyway.

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe     = Join-Path $here 'Kyklos.exe'
$install = Join-Path $env:LOCALAPPDATA 'Programs\Kyklos'
$target  = Join-Path $install 'Kyklos.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Kyklos.exe is not next to this script - unpack the whole zip to one folder first."
}

$running = Get-Process Kyklos -ErrorAction SilentlyContinue
if ($running) {
    "closing the running Kyklos"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

New-Item -ItemType Directory -Force -Path $install | Out-Null
Copy-Item -LiteralPath $exe -Destination $target -Force
"installed: $target ($([math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)) MB)"

# The two shell entries below are the same ones the app itself writes when configured, so an
# install made this way is indistinguishable from one the app set up from its own settings.
Set-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name Kyklos -Value "`"$target`"" -Force
"starts at login: yes (HKCU Run)"

$sendTo  = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\Convert with Kyklos.lnk'
$wsh     = New-Object -ComObject WScript.Shell
$lnk     = $wsh.CreateShortcut($sendTo)
$lnk.TargetPath      = $target
$lnk.Arguments       = '--sendto'
$lnk.WorkingDirectory = $install
$lnk.IconLocation    = "$target,0"
$lnk.Save()
"send to menu: Convert with Kyklos"

Start-Process -FilePath $target
"started"

# The exe is self-contained and needs nothing converted from images or PDFs. Everything else is
# honest about being missing until these are installed - same commands the app itself reports.
if (-not (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue)) {
    ""
    "ffmpeg is not installed: video and audio formats will show as unavailable until you run"
    "    winget install Gyan.FFmpeg"
    "then use the tray menu's check-tools item, or restart Kyklos."
}

if (-not (Test-Path (Join-Path $env:ProgramFiles 'LibreOffice\program\soffice.exe'))) {
    "LibreOffice is not installed: Office document formats will show as unavailable until you run"
    "    winget install TheDocumentFoundation.LibreOffice"
    "then use the tray menu's check-tools item, or restart Kyklos."
}
