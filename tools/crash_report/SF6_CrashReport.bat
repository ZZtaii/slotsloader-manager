@echo off
rem SF6 crash report - run this right after the game crashed.
rem It gathers the crash files into ONE zip on your Desktop and offers to send the report to the mods' team.
rem Nothing is sent before you answer Yes.
rem (c) the OVERDRIVE EDITION mods. The part after the #PS_BEGIN line is PowerShell, run by the line below.
setlocal
set "SF6CR_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = [IO.File]::ReadAllText($env:SF6CR_SELF); $i = $t.LastIndexOf('#PS_BEGIN'); Invoke-Expression $t.Substring($i)"
set "SF6CR_CODE=%ERRORLEVEL%"
if not defined SF6CR_QUIET pause
exit /b %SF6CR_CODE%

#PS_BEGIN
# ---------------------------------------------------------------------------------------------------------------------
# What goes in the zip (and nothing else):
#   report.txt                  when, Windows / CPU / GPU / memory, the crash lines of REFramework's log, the mods found
#   re2_framework_log.txt       REFramework's log of the last session (it holds the crash: exception, registers, stack)
#   sf6core.log                 the end of our mods' log
#   reframework_crash.dmp       the memory dump REFramework wrote at the crash (asked first: it holds the game's memory)
# Cleaned before zipping: the Windows user name, the PC's name and any SteamID are replaced in every text file.
# Sending (asked, after the zip is made): the same material goes to the mods' server as a crash report, on the channel of
# the in-game "Report a bug": POST <address>/api/report/crash with the sign-in that Report a bug saved on this PC
# (reframework\data\SF6_Community_data\identity.json: the server's address and a token; neither is ever printed). So the
# report is tied to the player's Steam account, as a bug report is. No sign-in on this PC, or one the server refuses:
# nothing is sent and the zip stays to be sent by hand. Sent: the zip is removed.
# Environment, for tests: SF6CR_GAME (the game's folder), SF6CR_OUT (where the zip goes), SF6CR_DUMP (1 / 0, no question),
# SF6CR_SEND (1 / 0, no question), SF6CR_API and SF6CR_TOKEN (in place of identity.json), SF6CR_QUIET (no pause, no Explorer).
# ---------------------------------------------------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$exe = 'StreetFighter6.exe'

function Find-Game {
    $c = New-Object System.Collections.Generic.List[string]
    if ($env:SF6CR_GAME) { $c.Add($env:SF6CR_GAME) }
    $d = Split-Path -Parent $env:SF6CR_SELF                       # the .bat may sit in the game's folder, or below it
    for ($i = 0; $i -lt 5 -and $d; $i++) { $c.Add($d); $d = Split-Path -Parent $d }
    $steam = $null
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            foreach ($n in 'SteamPath', 'InstallPath') { if ($p.$n) { $steam = $p.$n; break } }
        } catch {}
        if ($steam) { break }
    }
    if ($steam) {
        $steam = $steam -replace '/', '\'
        $c.Add((Join-Path $steam 'steamapps\common\Street Fighter 6'))
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $c.Add((Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Street Fighter 6'))
            }
        }
    }
    foreach ($p in $c) { if ($p -and (Test-Path -LiteralPath (Join-Path $p $exe))) { return (Resolve-Path -LiteralPath $p).Path } }
    return $null
}

# the Windows user name and the PC's name out of a text
function Clean([string]$s) {
    $s = [regex]::Replace($s, '(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n"<>|:*?]+', '${1}USER')
    foreach ($n in @($env:USERNAME, $env:COMPUTERNAME)) {
        if ($n -and $n.Length -ge 3) { $s = [regex]::Replace($s, [regex]::Escape($n), 'USER', 'IgnoreCase') }
    }
    return [regex]::Replace($s, '7656119\d{10}', '<id>')          # a SteamID never leaves in a text
}
function Last([string]$s, [int]$n) { if ($s -and $s.Length -gt $n) { return $s.Substring($s.Length - $n) } return $s }
function Tail([string]$path, [int]$lines) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return ((Get-Content -LiteralPath $path -Tail $lines -Encoding UTF8) -join "`r`n")
}
function Put([string]$dir, [string]$name, [string]$text) {
    [IO.File]::WriteAllText((Join-Path $dir $name), (Clean $text), (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host ''
Write-Host ' SF6 crash report' -ForegroundColor Cyan
Write-Host ' ----------------'
$game = Find-Game
if (-not $game) {
    Write-Host ' Street Fighter 6 was not found on this PC.' -ForegroundColor Yellow
    Write-Host ' Put this file in the game''s folder (the one with StreetFighter6.exe) and run it again.'
    exit 2
}
Write-Host " Game folder: $(Clean $game)"

$dmp = Join-Path $game 'reframework_crash.dmp'
$log = Join-Path $game 're2_framework_log.txt'
$core = Join-Path $game 'reframework\data\sf6core\sf6core.log'
$hasDmp = Test-Path -LiteralPath $dmp
$hasLog = Test-Path -LiteralPath $log
if (-not $hasLog -and -not $hasDmp) {
    Write-Host ' No REFramework log and no crash dump in that folder: there is nothing to report.' -ForegroundColor Yellow
    exit 3
}
$logText = if ($hasLog) { Get-Content -LiteralPath $log -Raw -Encoding UTF8 } else { '' }
# the crash as the log has it: its [error] lines, and the call stack's (a function's name, then "<module> + 0x<offset>")
$all = @($logText -split "`r?`n")
$crashLines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $all.Count; $i++) {
    $l = $all[$i]
    if ($l -match '\[error\]') { $crashLines.Add($l) }
    elseif ($l -match '^\s+\S.*\.(dll|exe) \+ 0x[0-9a-fA-F]+\s*$') {
        if ($i -gt 0 -and $all[$i - 1] -match '^\s+\S' -and $all[$i - 1] -notmatch '\[') { $crashLines.Add($all[$i - 1]) }
        $crashLines.Add($l)
    }
}
$logHasCrash = $logText -match 'Exception occurred'
if ($hasDmp) {
    $age = (Get-Date) - (Get-Item -LiteralPath $dmp).LastWriteTime
    Write-Host (" Crash dump: {0:yyyy-MM-dd HH:mm} ({1})" -f (Get-Item -LiteralPath $dmp).LastWriteTime, $(if ($age.TotalHours -lt 1) { '{0:n0} min ago' -f $age.TotalMinutes } elseif ($age.TotalDays -lt 2) { '{0:n0} h ago' -f $age.TotalHours } else { '{0:n0} days ago' -f $age.TotalDays }))
    if ($age.TotalHours -gt 24) { Write-Host ' That dump is more than a day old: is it the crash you mean?' -ForegroundColor Yellow }
} else { Write-Host ' Crash dump: none (the game may have closed without one)' }
if (-not $logHasCrash) {
    Write-Host ' REFramework''s log holds no crash: the game was started again since. The report is still made,' -ForegroundColor Yellow
    Write-Host ' but next time run this BEFORE starting the game again (the log is rewritten at each start).' -ForegroundColor Yellow
}

$withDmp = $false
if ($hasDmp) {
    if ($env:SF6CR_DUMP) { $withDmp = $env:SF6CR_DUMP -eq '1' }
    else {
        Write-Host ''
        Write-Host ' The crash dump is what tells us the most. It is a copy of part of the game''s memory at the crash.'
        $a = Read-Host ' Put it in the report? [Y/n]'
        $withDmp = -not ($a -match '^\s*n')
    }
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$work = Join-Path ([IO.Path]::GetTempPath()) "SF6_CrashReport_$stamp"
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # the mods found: name, size, date, version, the start of the SHA-256 (which build exactly)
    $mods = New-Object System.Collections.Generic.List[string]
    $files = @()
    foreach ($pat in 'dinput8.dll', 'reframework\plugins\*.dll') { $files += @(Get-ChildItem -Path (Join-Path $game $pat) -File -ErrorAction SilentlyContinue) }
    foreach ($f in $files) {
        $sha = try { (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 12).ToLower() } catch { '?' }
        $ver = try { $f.VersionInfo.FileVersion } catch { '' }
        $mods.Add(('{0,-34} {1,10} bytes  {2:yyyy-MM-dd HH:mm}  sha {3}  {4}' -f $f.Name, $f.Length, $f.LastWriteTime, $sha, $ver))
    }
    $lua = @(Get-ChildItem -Path (Join-Path $game 'reframework\autorun\*.lua') -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $data = @(Get-ChildItem -Path (Join-Path $game 'reframework\data') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    $os = try { Get-CimInstance Win32_OperatingSystem } catch { $null }
    $cpu = try { (Get-CimInstance Win32_Processor | Select-Object -First 1).Name } catch { '?' }
    $gpu = try { (Get-CimInstance Win32_VideoController | ForEach-Object { '{0} (driver {1})' -f $_.Name, $_.DriverVersion }) -join '; ' } catch { '?' }
    $r = New-Object System.Collections.Generic.List[string]
    $r.Add("SF6 crash report, made $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $r.Add("Game folder: $game")
    $r.Add("Crash dump: $(if ($hasDmp) { '{0:yyyy-MM-dd HH:mm:ss}, {1} bytes, {2}' -f (Get-Item -LiteralPath $dmp).LastWriteTime, (Get-Item -LiteralPath $dmp).Length, $(if ($withDmp) { 'in this report' } else { 'left out by the player' }) } else { 'none' })")
    $r.Add("REFramework log: $(if ($hasLog) { '{0:yyyy-MM-dd HH:mm:ss}, crash inside: {1}' -f (Get-Item -LiteralPath $log).LastWriteTime, $(if ($logHasCrash) { 'yes' } else { 'NO (the game was started again since)' }) } else { 'none' })")
    $r.Add('')
    $r.Add("Windows: $(if ($os) { '{0} build {1}' -f $os.Caption, $os.BuildNumber } else { '?' })")
    $r.Add("CPU: $cpu")
    $r.Add("GPU: $gpu")
    $r.Add("Memory: $(if ($os) { '{0:n1} GB, {1:n1} GB free' -f ($os.TotalVisibleMemorySize / 1MB), ($os.FreePhysicalMemory / 1MB) } else { '?' })")
    $r.Add('')
    $r.Add("Mods found ($($mods.Count)):")
    foreach ($m in $mods) { $r.Add("  $m") }
    $r.Add("Lua scripts in autorun ($($lua.Count)): $($lua -join ', ')")
    $r.Add("Data folders: $($data -join ', ')")
    $r.Add('')
    $r.Add("The crash, as REFramework's log has it ($($crashLines.Count) lines):")
    foreach ($l in $crashLines) { $r.Add("  $l") }
    Put $work 'report.txt' ($r -join "`r`n")
    if ($hasLog) { Put $work 're2_framework_log.txt' $logText }
    $t = Tail $core 4000
    if ($t) { Put $work 'sf6core.log' $t }
    if ($withDmp) { Copy-Item -LiteralPath $dmp -Destination (Join-Path $work 'reframework_crash.dmp') }

    $out = if ($env:SF6CR_OUT) { $env:SF6CR_OUT } else { [Environment]::GetFolderPath('Desktop') }
    if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Path $out -Force | Out-Null }
    $zip = Join-Path $out "SF6_CrashReport_$stamp.zip"
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
    Write-Host ''
    Write-Host ' Done. The report is here:' -ForegroundColor Green
    Write-Host "   $zip"
    Write-Host (' In it: {0} ({1:n0} KB).' -f ((Get-ChildItem -LiteralPath $work | ForEach-Object { $_.Name }) -join ', '), ((Get-Item -LiteralPath $zip).Length / 1KB))

    # ---- sending: the mods' server, with the sign-in the in-game Report a bug saved on this PC
    $api = $env:SF6CR_API
    $token = $env:SF6CR_TOKEN
    if (-not $api -or -not $token) {
        try {
            $idj = Get-Content -LiteralPath (Join-Path $game 'reframework\data\SF6_Community_data\identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if (-not $api) { $api = $idj.api_base }
            if (-not $token) { $token = $idj.token }
        } catch {}
    }
    $sent = $false
    $dumpLeftOut = $false                 # the dump was in the zip but too big to send: the zip is kept
    if (-not $api -or -not $token) {
        Write-Host ' This PC has no sign-in for the mods'' server (the in-game "Report a bug" makes it the first time it is opened).'
        Write-Host ' Nothing was sent: send this zip to the person who asked you for it, with what you were doing when it crashed.'
    } else {
        $send = $false
        if ($env:SF6CR_SEND) { $send = $env:SF6CR_SEND -eq '1' }
        else {
            Write-Host ''
            Write-Host ' Send this report to the mods'' team now? It goes to our server under your Steam account,'
            Write-Host ' as the in-game "Report a bug" does. Nothing else is sent.'
            $a = Read-Host ' Send? [Y/n]'
            $send = -not ($a -match '^\s*n')
        }
        if (-not $send) { Write-Host ' Nothing was sent. The zip is yours to send by hand.' }
        else {
            try {
                # the crash's identity: its dump (time and size), else the log's time: sending the same crash again replaces it
                $ref = if ($hasDmp) { Get-Item -LiteralPath $dmp } else { Get-Item -LiteralPath $log }
                $sha = [Security.Cryptography.SHA256]::Create()
                $hex = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(('{0}:{1}' -f $ref.LastWriteTimeUtc.Ticks, $ref.Length))) | ForEach-Object { $_.ToString('x2') })
                $body = [ordered]@{
                    crash_key      = 'crash:' + $hex.Substring(0, 16)
                    crashed_at     = [int64]([DateTimeOffset]$ref.LastWriteTimeUtc).ToUnixTimeSeconds()
                    client_version = 'crash-bat 1'
                    crash          = [ordered]@{
                        source        = 'SF6_CrashReport.bat'
                        log_has_crash = [bool]$logHasCrash
                        dump          = $(if (-not $hasDmp) { 'none' } elseif ($withDmp) { 'sent' } else { 'left out by the player' })
                        windows       = $(if ($os) { '{0} build {1}' -f $os.Caption, $os.BuildNumber } else { '?' })
                        cpu           = [string]$cpu
                        gpu           = [string]$gpu
                        memory_gb     = $(if ($os) { [math]::Round($os.TotalVisibleMemorySize / 1MB, 1) } else { 0 })
                        lines         = Last (Clean (($crashLines) -join "`n")) 9000
                    }
                    logs           = [ordered]@{
                        crash = Last (Clean $logText) 120000
                        core  = Last (Clean ([string]$t)) 120000
                    }
                    versions       = [ordered]@{}
                    mods           = @($mods | ForEach-Object { Clean $_ })
                    notes          = @()
                }
                foreach ($f in $files) { try { $body.versions[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 12).ToLower() } catch {} }
                if ($withDmp) {
                    $dz = Join-Path $work 'dump.zip'
                    Compress-Archive -LiteralPath $dmp -DestinationPath $dz -Force
                    if ((Get-Item -LiteralPath $dz).Length -le 450000) { $body.dump = [Convert]::ToBase64String([IO.File]::ReadAllBytes($dz)) }
                    else { $body.crash.dump = 'left out: its zip is over 450 KB'; $dumpLeftOut = $true; Write-Host ' The crash dump is too big for the server: the report goes without it, and the zip, which holds it, is kept.' -ForegroundColor Yellow }
                }
                $json = $body | ConvertTo-Json -Depth 6 -Compress
                $bytes = [Text.Encoding]::UTF8.GetBytes($json)
                if ($bytes.Length -gt 1900000) { throw 'the report is too large for the server' }
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                $r = Invoke-RestMethod -Uri ($api.TrimEnd('/') + '/api/report/crash') -Method Post -TimeoutSec 60 -UserAgent 'SF6-CrashReport/1' -ContentType 'application/json; charset=utf-8' `
                    -Headers @{ Authorization = 'Bearer ' + $token; 'x-client-version' = 'crash-bat 1' } -Body $bytes
                if ($r.ok) {
                    $sent = $true
                    Write-Host (' Sent ({0:n0} KB){1}. Thank you!' -f ($bytes.Length / 1KB), $(if ($r.replaced) { ', replacing the report already sent for this crash' } else { '' })) -ForegroundColor Green
                } else { Write-Host ' The server did not take the report.' -ForegroundColor Yellow }
            } catch {
                $code = 0
                try { $code = [int]$_.Exception.Response.StatusCode } catch {}
                $why = switch ($code) {
                    401 { 'the server no longer knows this PC''s sign-in: start the game, open "Report a bug" once, then run this again' }
                    426 { 'the server no longer knows this PC''s sign-in: start the game, open "Report a bug" once, then run this again' }
                    429 { 'this account already sent its crash reports for today' }
                    404 { 'the server does not take crash reports yet' }
                    413 { 'the report is too large for the server' }
                    0   { 'the server could not be reached (' + $_.Exception.Message + ')' }
                    default { "the server answered $code" }
                }
                Write-Host " Not sent: $why." -ForegroundColor Yellow
            }
        }
        # sent whole: the zip is no longer needed; sent without the dump: the zip stays, it is the only copy of the dump
        if ($sent -and -not $dumpLeftOut) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
        elseif ($sent) { Write-Host ' Sent without the crash dump: keep the zip on your Desktop, it holds the dump (we may ask you for it).' -ForegroundColor Yellow }
        else { Write-Host ' The zip stays on your Desktop: send it to the person who asked you for it.' }
    }
    if ((-not $sent -or $dumpLeftOut) -and -not $env:SF6CR_QUIET) { Start-Process explorer.exe "/select,`"$zip`"" }
    exit 0
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
