<#
.SYNOPSIS
    Richtet einen Windows-Rechner als OpenBench-Worker für Fispur ein.

.DESCRIPTION
    Installiert die Voraussetzungen (.NET SDK, Python, MSYS2 mit make/g++), holt den
    OpenBench-Client, fragt die Zugangsdaten ab und legt Start-Skript plus
    Desktop-Verknüpfung an. Jeder Schritt prüft zuerst, ob er nötig ist — das Skript
    lässt sich also gefahrlos erneut ausführen.

    Als Administrator ausführen, und zwar mit dem Konto, das den Worker später startet:
    das Passwort wird per DPAPI an Benutzerkonto und Rechner gebunden.

.PARAMETER Server
    URL der OpenBench-Instanz. Vorgabe: https://test.xenymor.com

.PARAMETER User
    OpenBench-Benutzername (wird sonst abgefragt).

.PARAMETER Password
    OpenBench-Passwort im Klartext. Nur nutzen, wenn die interaktive Eingabe Ärger macht
    (eingefügte Passwörter kommen dort je nach Konsole abgeschnitten an). Landet in der
    PowerShell-History - danach mit Clear-History bzw. Löschen von
    (Get-PSReadlineOption).HistorySavePath aufräumen.

.PARAMETER InstallRoot
    Zielverzeichnis. Vorgabe: %LOCALAPPDATA%\FispurWorker

.PARAMETER ClientSource
    Roh-URL der client.py aus DEINEM OpenBench-Fork. Der Client aktualisiert sich
    später selbst aus dem Repo, das der Server vorgibt.

.PARAMETER CheckOnly
    Nur prüfen und berichten, nichts installieren oder schreiben.

.PARAMETER ConfigureOnly
    Installationen überspringen, nur Zugangsdaten, Startskript und Verknüpfung neu anlegen.
    Nützlich, wenn das Passwort unter einem anderen Konto verschlüsselt wurde.

.PARAMETER SmokeTest
    Baut Fispur einmal testweise (make + bench), bevor der Worker startet.

.PARAMETER Force
    Installationsschritte auch dann ausführen, wenn die Werkzeuge bereits gefunden wurden.

.EXAMPLE
    .\setup-worker.ps1
.EXAMPLE
    .\setup-worker.ps1 -CheckOnly
.EXAMPLE
    .\setup-worker.ps1 -Server https://test.xenymor.com -User Xenymor -SmokeTest
#>

[CmdletBinding()]
param(
    [string] $Server       = 'https://test.xenymor.com',
    [string] $User         = '',
    [string] $Password     = '',
    [string] $InstallRoot  = (Join-Path $env:LOCALAPPDATA 'FispurWorker'),
    [string] $ClientSource = 'https://raw.githubusercontent.com/Xenymor/OpenBench/master/Client/client.py',
    [string] $Msys2Root    = 'C:\msys64',
    [string] $EngineFilter = 'Fispur',
    [switch] $CheckOnly,
    [switch] $ConfigureOnly,
    [switch] $SmokeTest,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------- helpers

function Write-Step  { param($Text) Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Write-Ok    { param($Text) Write-Host "  [ok]   $Text" -ForegroundColor Green }
function Write-Info  { param($Text) Write-Host "  [info] $Text" }
function Write-Warn  { param($Text) Write-Host "  [warn] $Text" -ForegroundColor Yellow }
function Write-Fail  { param($Text) Write-Host "  [fehl] $Text" -ForegroundColor Red }

# winget schreibt den PATH in die Registry, nicht in die laufende Sitzung. Ohne diesen
# Refresh findet das Skript die Programme nicht, die es gerade selbst installiert hat.
function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machine, $user) | Where-Object { $_ }) -join ';'
}

# Externe Programme laufen bewusst NICHT unter ErrorActionPreference 'Stop': Windows
# PowerShell verpackt jede stderr-Zeile eines nativen Programms in einen ErrorRecord und
# würde damit harmlose Warnungen (pip, gcc) in Abbrüche verwandeln. Maßgeblich ist der
# Exitcode.
function Invoke-Native {
    param([string] $Exe, [string[]] $Arguments = @())

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = @($output | ForEach-Object { $_.ToString() })
        }
    }
    finally { $ErrorActionPreference = $previous }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal] $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CommandPath {
    param([string] $Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# Erste Versionsnummer aus einer Programmausgabe ziehen ("g++ (Rev3) 13.2.0" -> 13.2.0)
function Get-VersionString {
    param([string] $Text)
    if ($Text -match '(\d+)\.(\d+)(\.\d+)?') { return $Matches[0] }
    return $null
}

function Invoke-Winget {
    param([string] $Id, [string] $Label)

    Write-Info "Installiere $Label via winget ($Id) ..."
    $wingetArgs = @(
        'install', '--id', $Id, '-e', '--source', 'winget',
        '--accept-package-agreements', '--accept-source-agreements',
        '--disable-interactivity'
    )
    $result = Invoke-Native -Exe 'winget' -Arguments $wingetArgs
    $result.Output | Out-Host

    # winget meldet 0x8A15002B ("bereits installiert") als Fehler - das ist keiner.
    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne -1978335189) {
        throw "winget konnte $Label nicht installieren (Exitcode $($result.ExitCode))."
    }
    Update-SessionPath
}

# --------------------------------------------------------------------------- detection

function Get-DotnetSdkMajor {
    $dotnet = Get-CommandPath 'dotnet'
    if (-not $dotnet) { return 0 }

    $result = Invoke-Native -Exe 'dotnet' -Arguments @('--list-sdks')
    if ($result.ExitCode -ne 0 -or -not $result.Output) { return 0 }

    $majors = foreach ($line in $result.Output) {
        if ($line -match '^(\d+)\.') { [int] $Matches[1] }
    }
    if (-not $majors) { return 0 }
    return ($majors | Measure-Object -Maximum).Maximum
}

function Get-PythonExe {
    # Der py-Launcher ist der verlässlichste Weg; sonst python.exe aus dem PATH.
    # Die App-Execution-Alias-Attrappe unter WindowsApps meldet keine Version.
    foreach ($candidate in @(@{ Exe = 'py'; Args = @('-3') }, @{ Exe = 'python'; Args = @() })) {
        $path = Get-CommandPath $candidate.Exe
        if (-not $path) { continue }

        # Keine Anführungszeichen im Python-Einzeiler: PowerShell entfernt sie beim
        # Übergeben an ein natives Programm, Python bekäme dann kaputten Code.
        $probeArgs = @($candidate.Args) + @('-c', 'import sys;print(sys.version.split()[0])')
        $result    = Invoke-Native -Exe $candidate.Exe -Arguments $probeArgs
        if ($result.ExitCode -ne 0) { continue }

        $ver = @($result.Output)[0]
        if ($ver -match '^(\d+)\.(\d+)') {
            $major = [int] $Matches[1]; $minor = [int] $Matches[2]
            if ($major -eq 3 -and $minor -ge 8) {
                return [pscustomobject]@{ Exe = $candidate.Exe; Args = $candidate.Args; Version = $ver }
            }
        }
    }
    return $null
}

function Get-Msys2Tools {
    param([string] $Root)
    $usrBin  = Join-Path $Root 'usr\bin'
    $mingw64 = Join-Path $Root 'mingw64\bin'
    return [pscustomobject]@{
        Root     = $Root
        UsrBin   = $usrBin
        Mingw64  = $mingw64
        Bash     = Join-Path $usrBin  'bash.exe'
        Make     = Join-Path $usrBin  'make.exe'
        Gpp      = Join-Path $mingw64 'g++.exe'
        HasBash  = Test-Path (Join-Path $usrBin  'bash.exe')
        HasMake  = Test-Path (Join-Path $usrBin  'make.exe')
        HasGpp   = Test-Path (Join-Path $mingw64 'g++.exe')
    }
}

# --------------------------------------------------------------------------- install steps

function Install-DotnetSdk {
    Write-Step '.NET SDK'
    $major = Get-DotnetSdkMajor
    if ($major -ge 8 -and -not $Force) {
        Write-Ok "SDK $major.x gefunden"
        return
    }
    if ($CheckOnly) { Write-Fail 'Kein .NET SDK >= 8 gefunden'; return }

    Invoke-Winget -Id 'Microsoft.DotNet.SDK.8' -Label '.NET SDK 8'

    $major = Get-DotnetSdkMajor
    if ($major -lt 8) { throw 'dotnet ist nach der Installation nicht auffindbar. Shell neu öffnen und erneut versuchen.' }
    Write-Ok "SDK $major.x installiert"
}

function Install-Python {
    Write-Step 'Python'
    $py = Get-PythonExe
    if ($py -and -not $Force) {
        Write-Ok "Python $($py.Version) gefunden ($($py.Exe))"
        return $py
    }
    if ($CheckOnly) { Write-Fail 'Kein Python >= 3.8 gefunden'; return $null }

    Invoke-Winget -Id 'Python.Python.3.12' -Label 'Python 3.12'

    $py = Get-PythonExe
    if (-not $py) { throw 'Python ist nach der Installation nicht auffindbar. Shell neu öffnen und erneut versuchen.' }
    Write-Ok "Python $($py.Version) installiert"
    return $py
}

function Install-Msys2 {
    Write-Step 'MSYS2 (make + g++ für den fastchess-Build)'

    $tools = Get-Msys2Tools -Root $Msys2Root
    if ($tools.HasMake -and $tools.HasGpp -and -not $Force) {
        Write-Ok "make und g++ gefunden in $Msys2Root"
        return $tools
    }
    if ($CheckOnly) {
        if (-not $tools.HasMake) { Write-Fail "make fehlt ($($tools.Make))" }
        if (-not $tools.HasGpp)  { Write-Fail "g++ fehlt ($($tools.Gpp))" }
        return $tools
    }

    if (-not $tools.HasBash) {
        Invoke-Winget -Id 'MSYS2.MSYS2' -Label 'MSYS2'
        $tools = Get-Msys2Tools -Root $Msys2Root
        if (-not $tools.HasBash) {
            throw "MSYS2 wurde nicht unter $Msys2Root gefunden. Mit -Msys2Root den tatsächlichen Pfad angeben."
        }
    }

    # Das erste -Syu aktualisiert ggf. den pacman-Kern und beendet die Shell dabei selbst;
    # ein von Null verschiedener Exitcode ist hier normal, deshalb zwei Durchläufe.
    Write-Info 'Aktualisiere die MSYS2-Paketdatenbank (kann einige Minuten dauern) ...'
    foreach ($pass in 1..2) {
        (Invoke-Native -Exe $tools.Bash -Arguments @('-lc', 'pacman -Syu --noconfirm')).Output | Out-Host
    }

    Write-Info 'Installiere make und mingw-w64-x86_64-gcc ...'
    $pacman = Invoke-Native -Exe $tools.Bash -Arguments @('-lc', 'pacman -S --needed --noconfirm make mingw-w64-x86_64-gcc')
    $pacman.Output | Out-Host

    $tools = Get-Msys2Tools -Root $Msys2Root
    if (-not ($tools.HasMake -and $tools.HasGpp)) {
        throw 'make oder g++ fehlen nach der pacman-Installation.'
    }
    Write-Ok "make und g++ bereit ($Msys2Root)"
    return $tools
}

function Install-PythonVenv {
    param($Python, [string] $VenvPath)

    Write-Step 'Python-Umgebung für den Client'

    $venvPy = Join-Path $VenvPath 'Scripts\python.exe'
    if ((Test-Path $venvPy) -and -not $Force) {
        Write-Ok "venv vorhanden ($VenvPath)"
    } else {
        if ($CheckOnly) { Write-Fail "venv fehlt ($VenvPath)"; return $null }
        Write-Info "Lege venv an: $VenvPath"
        $venvArgs = @($Python.Args) + @('-m', 'venv', $VenvPath)
        & $Python.Exe @venvArgs | Out-Host
        if (-not (Test-Path $venvPy)) { throw "venv konnte nicht angelegt werden ($VenvPath)." }
    }

    if ($CheckOnly) {
        $probe = Invoke-Native -Exe $venvPy -Arguments @('-c', 'import requests, psutil, cpuinfo')
        if ($probe.ExitCode -eq 0) { Write-Ok 'requests, psutil, py-cpuinfo vorhanden' }
        else { Write-Fail 'Client-Pakete fehlen im venv' }
        return $venvPy
    }

    # charset-normalizer steht explizit dabei: bei sehr neuen Python-Versionen fehlt es
    # sonst gelegentlich, und requests warnt dann bei jedem Start über fehlende
    # Zeichensatzerkennung.
    Write-Info 'Installiere requests, psutil, py-cpuinfo ...'
    (Invoke-Native -Exe $venvPy -Arguments @('-m', 'pip', 'install', '--upgrade', 'pip')).Output | Out-Host
    $pip = Invoke-Native -Exe $venvPy -Arguments @('-m', 'pip', 'install', 'requests', 'charset-normalizer', 'psutil', 'py-cpuinfo')
    $pip.Output | Out-Host
    if ($pip.ExitCode -ne 0) { throw 'pip konnte die Client-Pakete nicht installieren.' }

    Write-Ok 'Client-Pakete installiert'
    return $venvPy
}

function Install-Client {
    param([string] $Root)

    Write-Step 'OpenBench-Client'
    $target = Join-Path $Root 'client.py'

    if ((Test-Path $target) -and -not $Force) {
        Write-Ok "client.py vorhanden ($target)"
        return $target
    }
    if ($CheckOnly) { Write-Fail "client.py fehlt ($target)"; return $target }

    Write-Info "Lade $ClientSource"
    Invoke-WebRequest -Uri $ClientSource -OutFile $target -UseBasicParsing

    # client.py holt worker.py & Co. beim ersten Start selbst aus dem Repo, das der
    # Server vorgibt - mehr als diese eine Datei braucht der Bootstrap nicht.
    if (-not (Select-String -Path $target -Pattern 'OPENBENCH_USERNAME' -Quiet)) {
        throw "Die geladene Datei sieht nicht nach client.py aus. Stimmt -ClientSource?"
    }
    Write-Ok "client.py geladen ($target)"
    return $target
}

# --------------------------------------------------------------------------- configuration

function Read-WorkerConfig {
    param([string] $Root)

    Write-Step 'Zugangsdaten und Maschinendaten'

    $cpus    = @(Get-CimInstance Win32_Processor)
    $cores   = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
    $sockets = $cpus.Count
    if (-not $cores)   { $cores   = [Environment]::ProcessorCount }
    if (-not $sockets) { $sockets = 1 }

    $configPath = Join-Path $Root 'config.json'
    $existing   = $null
    if (Test-Path $configPath) { $existing = Get-Content $configPath -Raw | ConvertFrom-Json }

    $defaultUser   = if ($User)    { $User }    elseif ($existing) { $existing.username } else { '' }
    $defaultServer = if ($Server)  { $Server }  elseif ($existing) { $existing.server }   else { '' }

    $name = Read-Host "OpenBench-Benutzername [$defaultUser]"
    if (-not $name) { $name = $defaultUser }
    if (-not $name) { throw 'Ohne Benutzernamen geht es nicht.' }

    $url = Read-Host "Server-URL [$defaultServer]"
    if (-not $url) { $url = $defaultServer }

    # Read-Host -AsSecureString liest zeichenweise von der Tastatur. Eingefügte Passwörter
    # kommen je nach Konsolenhost gar nicht oder abgeschnitten an (ein mitkopierter
    # Zeilenumbruch beendet die Eingabe). -Password umgeht das.
    if ($Password) {
        $secret = ConvertTo-SecureString $Password -AsPlainText -Force
        Write-Info "Passwort aus -Password übernommen ($($Password.Length) Zeichen)"
    } else {
        Write-Info 'Strg+V fügt hier NICHTS ein (die Konsole liest rohe Tasten).'
        Write-Info 'Zum Einfügen: Rechtsklick, im Windows Terminal Strg+Umschalt+V - oder tippen.'

        $secret = $null
        foreach ($attempt in 1..3) {
            $candidate = Read-Host 'OpenBench-Passwort' -AsSecureString
            $check = (New-Object System.Management.Automation.PSCredential('x', $candidate)).GetNetworkCredential().Password

            if ($check.Length -ge 4) {
                Write-Info "Passwort erfasst ($($check.Length) Zeichen) - stimmt die Länge?"
                $secret = $candidate
                break
            }
            Write-Warn "Nur $($check.Length) Zeichen erfasst - vermutlich ist das Einfügen fehlgeschlagen."
        }

        if (-not $secret) { throw 'Passwort dreimal zu kurz erfasst. Alternativ mit -Password übergeben.' }
    }
    if ($secret.Length -eq 0) { throw 'Ohne Passwort geht es nicht.' }

    $threadDefault = if ($existing -and $existing.threads) { $existing.threads } else { $cores }
    $socketDefault = if ($existing -and $existing.sockets) { $existing.sockets } else { $sockets }

    Write-Info "Erkannt: $cores physische Kerne auf $sockets Sockel"
    $threads = Read-Host "Threads [$threadDefault]"
    if (-not $threads) { $threads = $threadDefault }
    $socketsIn = Read-Host "Sockel [$socketDefault]"
    if (-not $socketsIn) { $socketsIn = $socketDefault }

    # DPAPI: an dieses Benutzerkonto UND diesen Rechner gebunden. Auf einer anderen
    # Maschine oder unter anderem Konto ist die Datei wertlos - genau so soll es sein.
    $credPath = Join-Path $Root 'credentials.xml'
    (New-Object System.Management.Automation.PSCredential($name, $secret)) | Export-Clixml -Path $credPath
    Write-Ok "Passwort verschlüsselt abgelegt ($credPath)"

    $config = [ordered]@{
        username    = $name
        server      = $url
        threads     = [int] $threads
        sockets     = [int] $socketsIn
        engineOnly  = $EngineFilter
        msys2Root   = $Msys2Root
    }
    $config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
    Write-Ok "Konfiguration gespeichert ($configPath)"

    return $config
}

function Write-StartScript {
    param([string] $Root)

    Write-Step 'Startskript und Verknüpfung'

    $startPath = Join-Path $Root 'start-worker.ps1'

    $template = @'
# Startet den OpenBench-Worker. Erzeugt von setup-worker.ps1 - Änderungen gehen bei
# einem erneuten Setup-Lauf verloren.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$config = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
$cred   = Import-Clixml (Join-Path $root 'credentials.xml')

# MSYS2 nur für diesen Prozess in den PATH. Global gesetzt würden usr\bin\link.exe,
# find.exe und sort.exe die gleichnamigen Windows-Werkzeuge verdecken und andere
# Builds auf diesem Rechner beschädigen.
$msys = $config.msys2Root
$env:Path = (Join-Path $msys 'mingw64\bin') + ';' + (Join-Path $msys 'usr\bin') + ';' + $env:Path

# Über Umgebungsvariablen statt -U/-P/-S: so steht das Passwort nicht in der Prozessliste.
$env:OPENBENCH_USERNAME = $cred.UserName
$env:OPENBENCH_PASSWORD = $cred.GetNetworkCredential().Password
$env:OPENBENCH_SERVER   = $config.server

$python     = Join-Path $root '.venv\Scripts\python.exe'
$clientArgs = @('client.py', '-T', $config.threads, '-N', $config.sockets)
if ($config.engineOnly) { $clientArgs += @('--only', $config.engineOnly) }

Write-Host "Worker startet: $($config.username) @ $($config.server) mit $($config.threads) Threads" -ForegroundColor Cyan
Push-Location $root
try   { & $python @clientArgs }
finally {
    Pop-Location
    $env:OPENBENCH_PASSWORD = $null
}
'@

    if ($CheckOnly) {
        if (Test-Path $startPath) { Write-Ok "start-worker.ps1 vorhanden" } else { Write-Fail 'start-worker.ps1 fehlt' }
        return $startPath
    }

    Set-Content -Path $startPath -Value $template -Encoding UTF8
    Write-Ok "start-worker.ps1 geschrieben ($startPath)"

    $desktop  = [Environment]::GetFolderPath('Desktop')
    $linkPath = Join-Path $desktop 'Fispur Worker.lnk'
    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath       = (Get-Command powershell.exe).Source
    $shortcut.Arguments        = "-NoExit -ExecutionPolicy Bypass -File `"$startPath`""
    $shortcut.WorkingDirectory = $Root
    $shortcut.Description      = 'OpenBench-Worker für Fispur starten'
    $shortcut.Save()
    Write-Ok "Verknüpfung angelegt ($linkPath)"

    return $startPath
}

# --------------------------------------------------------------------------- verification

function Test-Installation {
    param($Tools, [string] $VenvPython, [string] $ClientPath)

    Write-Step 'Prüfung'

    $rows = @()

    $dotnetMajor = Get-DotnetSdkMajor
    $rows += [pscustomobject]@{ Werkzeug = 'dotnet SDK'; Status = if ($dotnetMajor -ge 8) { "OK ($dotnetMajor.x)" } else { 'FEHLT' } }

    $makeVer = $null
    if ($Tools.HasMake) { $makeVer = Get-VersionString ((Invoke-Native -Exe $Tools.Make -Arguments @('-v')).Output -join ' ') }
    $rows += [pscustomobject]@{ Werkzeug = 'make'; Status = if ($makeVer) { "OK ($makeVer)" } else { 'FEHLT' } }

    $gppVer = $null
    if ($Tools.HasGpp) { $gppVer = Get-VersionString ((Invoke-Native -Exe $Tools.Gpp -Arguments @('--version')).Output -join ' ') }
    $rows += [pscustomobject]@{ Werkzeug = 'g++'; Status = if ($gppVer) { "OK ($gppVer)" } else { 'FEHLT' } }

    $pkgStatus = 'FEHLT'
    if ($VenvPython -and (Test-Path $VenvPython)) {
        $probe = Invoke-Native -Exe $VenvPython -Arguments @('-c', 'import requests, psutil, cpuinfo')
        if ($probe.ExitCode -eq 0) { $pkgStatus = 'OK' }
    }
    $rows += [pscustomobject]@{ Werkzeug = 'Python-Pakete'; Status = $pkgStatus }

    $rows += [pscustomobject]@{ Werkzeug = 'client.py'; Status = if ($ClientPath -and (Test-Path $ClientPath)) { 'OK' } else { 'FEHLT' } }

    $rows | Format-Table -AutoSize | Out-Host

    return -not ($rows | Where-Object { $_.Status -eq 'FEHLT' })
}

function Invoke-SmokeTest {
    param($Tools, [string] $Root)

    Write-Step 'Smoke-Test: Fispur einmal bauen'

    $work = Join-Path $Root 'smoketest'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work | Out-Null

    $zip = Join-Path $work 'fispur.zip'
    Write-Info 'Lade Fispur (main) ...'
    Invoke-WebRequest -Uri 'https://github.com/Xenymor/Fispur/archive/main.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $work -Force

    $src = Get-ChildItem $work -Directory | Select-Object -First 1
    if (-not $src) { throw 'Das Fispur-Archiv enthielt kein Verzeichnis.' }

    $env:Path = $Tools.Mingw64 + ';' + $Tools.UsrBin + ';' + $env:Path

    Push-Location $src.FullName
    try {
        Write-Info 'make EXE=fispur-smoke ...'
        $build = Invoke-Native -Exe $Tools.Make -Arguments @('-j', 'EXE=fispur-smoke')
        $build.Output | Out-Host
        if ($build.ExitCode -ne 0) { throw 'Der Build ist fehlgeschlagen - siehe Ausgabe oben.' }

        $exe = Join-Path $src.FullName 'fispur-smoke.exe'
        if (-not (Test-Path $exe)) { throw 'make lief durch, aber fispur-smoke.exe fehlt.' }

        Write-Info 'bench ...'
        $bench = Invoke-Native -Exe $exe -Arguments @('bench')
        $last  = @($bench.Output)[-1]
        Write-Host "  $last"
        if ($last -notmatch '\d+\s+nodes\s+\d+\s+nps') {
            throw 'Der Bench hat nicht im erwarteten Format geantwortet.'
        }
        Write-Ok 'Build und Bench in Ordnung'
    }
    finally {
        Pop-Location
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --------------------------------------------------------------------------- main

Write-Host ''
Write-Host 'Fispur / OpenBench - Worker-Setup' -ForegroundColor White
Write-Host "Zielverzeichnis: $InstallRoot"

if (-not [Environment]::Is64BitOperatingSystem) { throw 'Es wird ein 64-Bit-Windows benötigt.' }

$needsInstalls = -not ($CheckOnly -or $ConfigureOnly)

if ($needsInstalls -and -not (Test-Admin)) {
    throw @'
Bitte als Administrator ausführen (PowerShell mit Rechtsklick -> "Als Administrator ausführen"),
und zwar mit demselben Benutzerkonto, das den Worker später startet - das Passwort wird an
Konto und Rechner gebunden.
'@
}

if ($needsInstalls -and -not (Get-CommandPath 'winget')) {
    throw @'
winget wurde nicht gefunden. Es gehört zum "App Installer" aus dem Microsoft Store
(Windows 10 1809+). Entweder nachinstallieren, oder .NET SDK 8, Python 3 und MSYS2
von Hand einrichten und das Skript dann mit -ConfigureOnly erneut aufrufen.
'@
}

Update-SessionPath

if (-not $CheckOnly -and -not (Test-Path $InstallRoot)) {
    New-Item -ItemType Directory -Path $InstallRoot | Out-Null
}

$tools      = $null
$python     = $null
$venvPython = $null
$clientPath = Join-Path $InstallRoot 'client.py'

if ($ConfigureOnly) {
    Write-Info 'ConfigureOnly: Installationsschritte werden übersprungen.'
    $tools      = Get-Msys2Tools -Root $Msys2Root
    $venvPython = Join-Path $InstallRoot '.venv\Scripts\python.exe'
} else {
    Install-DotnetSdk
    $python = Install-Python
    $tools  = Install-Msys2

    if (-not $CheckOnly) {
        $venvPython = Install-PythonVenv -Python $python -VenvPath (Join-Path $InstallRoot '.venv')
        $clientPath = Install-Client -Root $InstallRoot
    } else {
        $venvPython = Join-Path $InstallRoot '.venv\Scripts\python.exe'
        Install-PythonVenv -Python $python -VenvPath (Join-Path $InstallRoot '.venv') | Out-Null
        Install-Client -Root $InstallRoot | Out-Null
    }
}

$ok = Test-Installation -Tools $tools -VenvPython $venvPython -ClientPath $clientPath

if ($CheckOnly) {
    Write-Host ''
    if ($ok) { Write-Ok 'Alle Voraussetzungen erfüllt.' } else { Write-Warn 'Es fehlt etwas - Skript ohne -CheckOnly ausführen.' }
    return
}

if (-not $ok) { throw 'Es fehlen Voraussetzungen (siehe Tabelle). Setup abgebrochen.' }

if ($SmokeTest) { Invoke-SmokeTest -Tools $tools -Root $InstallRoot }

$config = Read-WorkerConfig -Root $InstallRoot
$start  = Write-StartScript -Root $InstallRoot

Write-Host ''
Write-Host 'Fertig.' -ForegroundColor Green
Write-Host "  Worker starten:  Desktop-Verknüpfung 'Fispur Worker'"
Write-Host "  oder von Hand:   powershell -ExecutionPolicy Bypass -File `"$start`""
Write-Host ''
Write-Host "Im Startlog muss '$EngineFilter | dotnet (8.0.x)' erscheinen; danach taucht der"
Write-Host "Rechner unter $($config.server)/machines/ auf."
