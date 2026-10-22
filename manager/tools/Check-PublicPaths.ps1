# 2.4.3: fails the build when shipping source, docs or the Assistant knowledge pack contain a personal or local
# path. Real-archive checks read the developer's folders from the untracked tests.local.json (LocalTestConfig.cs),
# so nothing personal needs to be in a tracked file. Allow-list: files that may name such a path because they only
# read it from the local config; it is empty today and should stay small.
param([Parameter(Mandatory = $true)][string]$Project, [string[]]$Extra = @())
$ErrorActionPreference = 'Stop'
$patterns = @('C:\Users\')
# The developer's own folders are listed in the untracked tests.local.json (PrivatePatterns), never in this file.
$localConfig = Join-Path $Project 'tests.local.json'
if (Test-Path -LiteralPath $localConfig) { $private = (Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json).PrivatePatterns; if ($private) { $patterns += @($private) } }
$allow = @()   # e.g. 'src\SomeRealCheck.cs'
$roots = @('src', 'docs', 'tests', 'assets', 'tools', 'linux')
$files = @()
foreach ($r in $roots) { $dir = Join-Path $Project $r; if (Test-Path -LiteralPath $dir) { $files += Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Extension -in '.cs', '.md', '.ps1', '.json', '.py', '.txt', '.config', '.manifest', '.yml', '.sh', '.desktop' -and $_.FullName -notmatch '\\(bin|obj)\\' } } }
foreach ($name in 'README.md', 'AGENTS.md', 'test.ps1', 'build.ps1', 'tests.local.example.json', 'SF6-SlotsLoader-Installer.csproj') { $f = Join-Path $Project $name; if (Test-Path -LiteralPath $f) { $files += Get-Item -LiteralPath $f } }
foreach ($e in $Extra) { if (Test-Path -LiteralPath $e) { $files += Get-Item -LiteralPath $e } }
$hits = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($Project.TrimEnd('\').Length + 1)
    if ($allow -contains $relative -or $relative -like 'tests\*.txt' -or $relative -eq 'tools\Check-PublicPaths.ps1') { continue }   # this script names the patterns   # tests\*.txt: local run logs, not published
    $text = [IO.File]::ReadAllText($file.FullName)
    # Also as written in JSON or a non-verbatim C# string (doubled backslashes) and with forward slashes.
    foreach ($p in ($patterns + ($patterns | ForEach-Object { $_.Replace('\', '\\') }) + ($patterns | ForEach-Object { $_.Replace('\', '/') }))) {
        $i = $text.IndexOf($p, [StringComparison]::OrdinalIgnoreCase)
        if ($i -ge 0) { $line = ($text.Substring(0, $i) -split "`n").Count; $hits += "$relative`:$line contains '$p'" }
    }
}
if ($hits.Count) { $hits | ForEach-Object { Write-Error $_ -ErrorAction Continue }; throw ('Personal paths in shipping files (' + $hits.Count + '). Use <game folder>, <Fluffy>\Games\SF6\Mods, <Manager folder>, or tests.local.json.') }
'Public path check: ' + $files.Count + ' files clean'
