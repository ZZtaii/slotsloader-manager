# Builds the Assistant's knowledge pack (embedded in both executables as SlotsInstaller.AssistantKnowledge.md)
# from the user-facing docs only: README (What changed limited to the two newest versions), docs/HELP.md, the two
# newest sections of docs/UI-REFERENCE.md, docs/CLI.md, and the Slotsloader repository's README.md and
# COMPATIBILITY.md. Developer docs (DEVELOPMENT, API, ARCHITECTURE, TESTING, handoffs) are never read.
# Deterministic: the same input bytes give the same output bytes (no dates, LF line ends, UTF-8 without BOM).
# Fails when text about the owned-outfit opt-out (or its Manager-side marker) would end up in the pack.
param([Parameter(Mandatory = $true)][string]$Project, [Parameter(Mandatory = $true)][string]$Output)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Read-Doc([string]$path) { if (-not (Test-Path -LiteralPath $path)) { return $null }; return ([IO.File]::ReadAllText($path, $utf8)).Replace("`r`n", "`n").Replace("`r", "`n") }
$versionBullet = '^- (\d+\.\d+\.\d+)( |\(|:)'   # 1.0.11: the README writes "- 1.0.11:" (the colon wasn't matched, so no version was ever left out)

function Limit-WhatChanged([string]$text, [int]$keep) {
    $out = New-Object System.Collections.Generic.List[string]; $inSection = $false; $seen = 0; $dropping = $false
    foreach ($line in $text.Split("`n")) {
        if ($line -match '^## ') { $inSection = ($line -match '^## What changed'); $dropping = $false }
        elseif ($inSection -and $line -match $versionBullet) { $seen++; $dropping = $seen -gt $keep }
        if ($dropping) { continue }
        $out.Add($line)
    }
    return ($out -join "`n")
}
function Sections([string]$text, [string[]]$keepPatterns, [int]$newestVersions) {
    # Keeps the named "## " sections and the first N "## <version> ..." sections (in document order).
    $out = New-Object System.Collections.Generic.List[string]; $keep = $false; $versions = 0
    foreach ($line in $text.Split("`n")) {
        if ($line -match '^## ') {
            $keep = $false
            foreach ($p in $keepPatterns) { if ($line -match $p) { $keep = $true } }
            if ($line -match '^## \d+\.\d+\.\d+ ') { $versions++; if ($versions -le $newestVersions) { $keep = $true } }
        }
        if ($keep) { $out.Add($line) }
    }
    return ($out -join "`n")
}
function Clean([string]$text) {
    $lines = foreach ($line in $text.Split("`n")) {
        if ($line -match '^\s*\[?!\[') { continue }                         # images and badges
        if ($line -match '<!--') { continue }
        # No hashes and no absolute local paths (the docs name the author's folders; the model never needs them).
        (($line -replace '\b[0-9A-Fa-f]{64}\b', '<sha256>') -replace '\b[A-Za-z]:\\[^\s`''"<>|*?)]*', '<folder>').TrimEnd()
    }
    $joined = ($lines -join "`n")
    while ($joined.Contains("`n`n`n")) { $joined = $joined.Replace("`n`n`n", "`n`n") }
    return $joined.Trim()
}

# 2.5.1: the loader's docs name another mod manager; the pack (and so the Assistant) says "external mod manager".
function Neutral([string]$text) {
    foreach ($pair in @(@('Fluffy Mod Manager', 'an external mod manager'), @("Fluffy's", "the external mod manager's"), @('Fluffy-installed', 'externally installed'),
                        @('\*\*Fluffy:\*\*', '**External mod managers:**'), @('\bFluffy (?=(mod|bundle|bundles|pak|preset|coexistence)\b)', 'mod-manager '), @('\bFluffy coexistence', 'mod-manager coexistence'),
                        @('\b(for|like) Fluffy\b', '$1 mod managers'), @('\b(the|The) Fluffy\b', '$1 external mod manager'), @('\bFluffy\b', 'an external mod manager'))) { $text = $text -replace $pair[0], $pair[1] }
    return $text
}
# The loader's docs: the repository root when the app is its manager\ folder (the public repository), else the
# loader worktree beside the app's folder (the development workspace).
$parentDir = Split-Path -Parent $Project
$loaderRepo = if (Test-Path -LiteralPath (Join-Path $parentDir 'COMPATIBILITY.md')) { $parentDir } else { Join-Path $parentDir 'SF6-SlotsLoader-1.11-loader' }
$parts = New-Object System.Collections.Generic.List[string]
$parts.Add("# Slotsloader Manager and Slotsloader: user guide`n`nThe documentation of the app (Slotsloader Manager) and of the Slotsloader loader, for answering how-to and troubleshooting questions. Live state (what is installed, versions, health) comes from the app's tools, not from this text.")
$readme = Read-Doc (Join-Path $Project 'README.md')
# 1.0.11: What changed keeps the two newest versions (docs/HELP.md describes today's behaviour), within the 26k-token budget.
if ($readme) { $parts.Add("# Source: Slotsloader Manager README`n`n" + (Clean (Limit-WhatChanged $readme 2))) }
# 1.0.11: the in-app Help articles (docs/HELP.md, gear → Help); the format comment at its top is left out.
$help = Read-Doc (Join-Path $Project 'docs\HELP.md')
# Links read as their shown text, as on the page: [[Topic|text]] → text, [[Topic#Heading]] → Heading, [[Topic]] → Topic.
if ($help) {
    $help = [regex]::Replace($help, '(?s)<!--.*?-->', '')
    $help = [regex]::Replace($help, '\[\[[^\]|]*\|([^\]]*)\]\]', '$1'); $help = [regex]::Replace($help, '\[\[[^\]#]*#([^\]]*)\]\]', '$1'); $help = [regex]::Replace($help, '\[\[([^\]]*)\]\]', '$1')
    $parts.Add("# Source: Slotsloader Manager Help (the in-app Help page, gear → Help or F1)`n`n" + (Clean $help))
}
$ui = Read-Doc (Join-Path $Project 'docs\UI-REFERENCE.md')
# 1.0.11: two newest version sections (was three): docs/HELP.md now covers the user-facing behaviour, within the 26k-token budget.
if ($ui) { $parts.Add("# Source: Slotsloader Manager screens (UI reference, newest versions)`n`n" + (Clean (Sections $ui @('^## Views and how to reach them') 2))) }
$cli = Read-Doc (Join-Path $Project 'docs\CLI.md')
if ($cli) { $parts.Add("# Source: Slotsloader CLI.exe`n`n" + (Clean $cli)) }
$loaderReadme = Read-Doc (Join-Path $loaderRepo 'README.md')
if ($loaderReadme) { $parts.Add("# Source: Slotsloader (the loader) README`n`n" + (Clean (Neutral $loaderReadme))) }
$compat = Read-Doc (Join-Path $loaderRepo 'COMPATIBILITY.md')
if ($compat) { $parts.Add("# Source: Slotsloader compatibility notes (known mod issues)`n`n" + (Clean (Neutral $compat))) }
$pack = ($parts -join "`n`n") + "`n"

$excluded = 'show_unowned|unowned_slots|opt-out marker|private opt-out|owned-outfit opt-out|owned outfit opt-out|CostumeSlots_data|bypass the owned|skip the owned'
$hits = [regex]::Matches($pack, $excluded, 'IgnoreCase')
if ($hits.Count -gt 0) { throw ('The Assistant knowledge pack would contain excluded text: ' + (($hits | ForEach-Object { $_.Value } | Sort-Object -Unique) -join ', ')) }
foreach ($required in @('# Source: Slotsloader Manager README', '# Source: Slotsloader CLI.exe', '## What changed')) { if (-not $pack.Contains($required)) { throw "The Assistant knowledge pack lacks '$required'." } }

New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
[IO.File]::WriteAllText($Output, $pack, $utf8)
Write-Output ("Assistant knowledge pack: " + $pack.Length + " characters (about " + [int]($pack.Length / 4) + " tokens at 4 characters per token), " + ($parts.Count - 1) + " sources")
