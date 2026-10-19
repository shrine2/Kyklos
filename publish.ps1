# Publishes Kyklos into the per-user install folder and brings the tray back up.
#
#   .\publish.ps1             publish, then start Kyklos
#   .\publish.ps1 -NoStart    publish only, leave it stopped
#
# The install folder is where the login entry and the Send To shortcut resolve to, and both are
# refreshed here so they can never quietly point at a previous copy.

param([switch]$NoStart)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\Kyklos\Kyklos.csproj'
$install = Join-Path $env:LOCALAPPDATA 'Programs\Kyklos'
$exe     = Join-Path $install 'Kyklos.exe'

# Windows will not replace a running executable, so the tray has to be closed first.
$running = Get-Process Kyklos -ErrorAction SilentlyContinue
if ($running) {
    "closing the running Kyklos"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

New-Item -ItemType Directory -Force -Path $install | Out-Null

dotnet publish $project `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $install

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

"installed: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"

# Restarting the shell integration here keeps it correct even after the exe moves: it resolves
# to whatever is running, and a publish does not touch the registry, so refresh both entries.
$runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name Kyklos -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $runKey -Name Kyklos -Value "`"$exe`""
    "login entry: $=$((Get-ItemProperty -Path $runKey -Name Kyklos).Kyklos)"
}

$sendTo = Join-Path ($env:APPDATA) 'Microsoft\Windows\SendTo\Convert with Kyklos.lnk'
if (Test-Path $sendTo) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($sendTo)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = '--sendto'
    $shortcut.WorkingDirectory = $install
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Save()
    "send-to shortcut target: $($shell.CreateShortcut($sendTo).TargetPath)"
}

if ($NoStart) { return }

# A freshly written unsigned exe can be refused once on first sight; the retry works. Anything
# else in that exception usually deserves to be looked at, so all three failures are reported.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        Start-Process -FilePath $exe
        "started"
        return
    }
    catch {
        "start attempt $attempt refused: $($_.Exception.Message.Trim())"
        Start-Sleep -Seconds 10
    }
}

throw "Kyklos never started - run it once by hand and see whether Windows asks to allow it."
