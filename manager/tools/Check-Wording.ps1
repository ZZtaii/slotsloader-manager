# 2.5.1: fails the build when the user docs or the Assistant knowledge pack name another product ("Fluffy"; the
# docs say "external mod manager"). Identifiers stay as they are in code spans without spaces (`FluffyInstalls`,
# `fluffy-removed`, the old `fluffy-status` alias); everything else is prose and must not contain the word.
param([Parameter(Mandatory = $true)][string]$Project, [string[]]$Extra = @())
$ErrorActionPreference = 'Stop'
$files = @((Join-Path $Project 'README.md'), (Join-Path $Project 'src\API.md')) + @(Get-ChildItem -LiteralPath (Join-Path $Project 'docs') -Filter '*.md' | ForEach-Object { $_.FullName }) + $Extra
$hits = @()
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        $n++
        $prose = [regex]::Replace($line, '`[^`\s]+`', '')
        if ($prose -match '(?i)fluffy') { $hits += ((Split-Path -Leaf $file) + ':' + $n + ': ' + $line.Trim()) }
    }
}
if ($hits.Count) { $hits | Select-Object -First 40 | ForEach-Object { Write-Error $_ -ErrorAction Continue }; throw ('"Fluffy" in user docs (' + $hits.Count + ' lines). Say "external mod manager"; identifiers go in `code`.') }
'Wording check: ' + $files.Count + ' docs clean'
