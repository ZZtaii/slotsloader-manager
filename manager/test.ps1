# -Only runs just the named checks (any of those below, real ones included); -Dialogs adds the visible
# real-app dialog check, which is left out by default since the 2.2.0 sizing fix.
# Real-archive checks read your folders from -LocalConfig (default tests.local.json beside this script; untracked,
# see tests.local.example.json): GameRoot, Libraries, FluffyRoots, Archives.juri and so on. Nothing personal is in the source.
param([string]$OutputDirectory = '', [switch]$RealArchives, [switch]$Dialogs, [string[]]$Only, [string]$LocalConfig = '')
$ErrorActionPreference = 'Stop'
$testProject = Split-Path -Parent $PSCommandPath
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $testProject ('out\tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
# The self-test writes only inside the folder that holds this run's new output directory.
$testWorkspace = Split-Path -Parent ([IO.Path]::GetFullPath($OutputDirectory))
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory for an isolated test run.' }
& (Join-Path $testProject 'build.ps1') -OutputDirectory $OutputDirectory
$testExe = Join-Path $OutputDirectory 'Slotsloader Manager.exe'
$testFixtures = Join-Path $OutputDirectory 'fixtures'
if (-not $LocalConfig) { $LocalConfig = Join-Path $testProject 'tests.local.json' }
$env:SLOTSLOADER_TEST_CONFIG = $LocalConfig; $env:SLOTSLOADER_PROJECT = $testProject; $env:SLOTSLOADER_WORKSPACE = $testWorkspace; $env:SLOTSLOADER_NO_DETECT = '1'
$local = if (Test-Path -LiteralPath $LocalConfig) { Get-Content -LiteralPath $LocalConfig -Raw | ConvertFrom-Json } else { $null }
$realGame = if ($local) { $local.GameRoot } else { '<set GameRoot in tests.local.json>' }
$realJuri = if ($local -and $local.Archives.juri) { $local.Archives.juri } else { '<set Archives.juri in tests.local.json>' }
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'Data') -Force | Out-Null
@{ GameRoot = (Join-Path $testFixtures 'game'); Libraries = @(); SevenZipPath = 'C:\Program Files\7-Zip\7z.exe' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'Data\settings.json') -Encoding UTF8
$testCases = @(
    @{ Name='generated'; Arguments=@('--self-test-extra', ('"' + $testFixtures + '"')) },
    @{ Name='outfit-sets'; Arguments=@('--set-check', ('"' + $testFixtures + '"')) },
    @{ Name='outfit-set-ui'; Arguments=@('--set-ui-check', ('"' + $testFixtures + '"')) },
    @{ Name='layout'; Arguments=@('--layout-check') },
    @{ Name='smoke'; Arguments=@('--smoke-ui') },
    @{ Name='fields'; Arguments=@('--field-check', ('"' + (Join-Path $OutputDirectory 'field-check') + '"')) },
    @{ Name='ui-polish'; Arguments=@('--polish-check', ('"' + $testFixtures + '"')) },
    @{ Name='assistant'; Arguments=@('--assistant-check', ('"' + $testFixtures + '"')) },
    @{ Name='assistant-ui'; Arguments=@('--assistant-ui-check', ('"' + $testFixtures + '"')) },
    @{ Name='replace'; Arguments=@('--replace-check', ('"' + $testFixtures + '"')) },
    @{ Name='replace-ui'; Arguments=@('--replace-ui-check', ('"' + $testFixtures + '"')) },
    @{ Name='filters'; Arguments=@('--filter-check', ('"' + (Join-Path $OutputDirectory 'filter-check') + '"')) },
    @{ Name='loader'; Arguments=@('--loader-check', ('"' + (Join-Path $OutputDirectory 'loader-check') + '"')) },
    @{ Name='updates'; Arguments=@('--updates-check', ('"' + (Join-Path $OutputDirectory 'updates-check') + '"')) },
    @{ Name='history'; Arguments=@('--history-check', ('"' + (Join-Path $OutputDirectory 'history-check') + '"')) },
    @{ Name='library-delete'; Arguments=@('--delete-check', ('"' + (Join-Path $OutputDirectory 'delete-check') + '"')) },
    @{ Name='variants'; Arguments=@('--variants-check', ('"' + $testFixtures + '"')) },
    @{ Name='slots'; Arguments=@('--slots-check', ('"' + $testFixtures + '"')) },
    @{ Name='wording'; Arguments=@('--wording-check', ('"' + $testFixtures + '"')) }
)
$dialogCase = @{ Name='dialog'; Arguments=@('--dialog-check', ('"' + (Join-Path $OutputDirectory 'dialog-check') + '"')); Style='Normal' }
$realCases = @(
    @{Name='real-backend'; Arguments=@('--self-test', ('"' + $testFixtures + '"'))},
    @{Name='bundle-ui'; Arguments=@('--exercise-ui', ('"' + $realJuri + '"'))},
    @{Name='bundle-layout'; Arguments=@('--layout-check', ('"' + $realJuri + '"'))},
    @{Name='replace-real'; Arguments=@('--replace-real-check', ('"' + (Join-Path $OutputDirectory 'replace-real') + '"'))},
    @{Name='filter-real'; Arguments=@('--filter-real-check', ('"' + (Join-Path $OutputDirectory 'filter-real') + '"'), ('"' + $realGame + '"'))},
    @{Name='loader-real'; Arguments=@('--loader-real-check', ('"' + (Join-Path $OutputDirectory 'loader-real') + '"'))},
    @{Name='history-real'; Arguments=@('--history-real-check', ('"' + (Join-Path $OutputDirectory 'history-real') + '"'), ('"' + $realGame + '"'))},
    @{Name='bundle-polish'; Arguments=@('--polish-check', ('"' + $testFixtures + '"'), ('"' + $realJuri + '"'))}
)
if ($Only) {
    $allCases = $testCases + $realCases + @($dialogCase)
    $wanted = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($wanted | Where-Object { $allCases.Name -notcontains $_ })
    if ($unknown.Count) { throw ('Unknown check: ' + ($unknown -join ', ') + '. Known: ' + ($allCases.Name -join ', ')) }
    $testCases = @($allCases | Where-Object { $wanted -contains $_.Name })
} else {
    if ($RealArchives -and -not $local) { throw ('-RealArchives needs ' + $LocalConfig + ': copy tests.local.example.json and fill in your folders.') }
    if ($RealArchives) { $testCases += $realCases }
    if ($Dialogs) { $testCases += $dialogCase }
}
foreach ($testCase in $testCases) {
    $testProcess = Start-Process -FilePath $testExe -ArgumentList $testCase.Arguments -WindowStyle $(if ($testCase.Style) { $testCase.Style } else { 'Hidden' }) -Wait -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory ($testCase.Name + '-stdout.txt')) -RedirectStandardError (Join-Path $OutputDirectory ($testCase.Name + '-stderr.txt'))
    Write-Output ($testCase.Name + ' exit: ' + $testProcess.ExitCode)
    if ($testProcess.ExitCode -ne 0) { throw ('Check failed: ' + $testCase.Name + '. Read reports and Data/log.txt in ' + $OutputDirectory) }
}
Write-Output ('All requested checks passed. Reports and generated fixtures: ' + $OutputDirectory)
