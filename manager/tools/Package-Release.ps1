# 2.5.9: builds the Manager release zip with the approved Slotsloader inside it. 1.0.19: the approved one is the
# slots_loader entry of the repository's dependencies.json (one source of truth: build.ps1 embeds the same file).
#  1. Reads ..\dependencies.json (format 1, source Wael3rd/SF6-ModSuite) and its slots_loader entry.
#  2. Downloads that file from https://github.com/<source>/releases/download/<release>/<file> (or takes -LoaderZip) and
#     checks its size and SHA-256 against the list. Nothing is rewritten.
#  3. Builds (build.ps1 embeds the list) and packs Slotsloader-Manager-<version>-portable.zip: the Manager exe, its
#     config and README.md at the root plus
#     Slotsloader\<loader zip>, then SHA256SUMS.txt beside it.
#  4. 1.0.11: 7-Zip is NOT in the zip: a Manager 1.0.10 or older refuses an update zip with a 7-Zip folder ("unexpected
#     files"), and someone can update from any older version. The Manager fetches its pinned 7-Zip release
#     (SevenZipTool.Supported, tag tool-7zip-26.00) when no usable 7-Zip is installed. -BundleSevenZip still puts
#     7-Zip\7z.exe, 7z.dll and License.txt in (x64, unmodified, from -SevenZipDir, default: the installed 7-Zip), for
#     a zip that is never offered as an update.
# Nothing is published: the commands to do that are printed at the end.
# -LoaderZip <path>: use this local zip instead of downloading (it must match the list). -NoLoaderFetch: never
#  download (offline); needs -LoaderZip or a cached copy in <output>\loader-cache.
param([string]$OutputDirectory = '', [switch]$NoLoaderFetch, [string]$LoaderZip = '', [string]$TestReport = '', [string]$SevenZipDir = '', [switch]$BundleSevenZip)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$repo = 'ZZtaii/slotsloader-manager'
$listFile = Join-Path (Split-Path -Parent $project) 'dependencies.json'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project ('out\release-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw "Choose a new or empty output directory: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cache = Join-Path $OutputDirectory 'loader-cache'; New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Get-Sha256([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

# 4 (checked first, so a missing 7-Zip stops before anything is downloaded or rewritten).
$sevenFiles = @('7z.exe', '7z.dll', 'License.txt'); $sevenVersion = ''
if ($BundleSevenZip) {
    if (-not $SevenZipDir) {
        $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\7-Zip' -ErrorAction SilentlyContinue
        $SevenZipDir = if ($key -and $key.Path64) { $key.Path64 } elseif ($key -and $key.Path) { $key.Path } else { Join-Path $env:ProgramW6432 '7-Zip' }
    }
    foreach ($f in $sevenFiles) { if (-not (Test-Path -LiteralPath (Join-Path $SevenZipDir $f))) { throw "7-Zip: $f is missing in $SevenZipDir (pass -SevenZipDir, or leave out -BundleSevenZip)." } }
    $sevenInfo = (Get-Item -LiteralPath (Join-Path $SevenZipDir '7z.exe')).VersionInfo
    if ($sevenInfo.CompanyName -ne 'Igor Pavlov') { throw "7-Zip: $SevenZipDir\7z.exe is not an official 7-Zip build (company '$($sevenInfo.CompanyName)')." }
    $machine = [IO.File]::ReadAllBytes((Join-Path $SevenZipDir '7z.exe')); $pe = [BitConverter]::ToInt32($machine, 0x3C)
    if ([BitConverter]::ToUInt16($machine, $pe + 4) -ne 0x8664) { throw "7-Zip: $SevenZipDir\7z.exe is not the x64 build." }
    $sevenVersion = $sevenInfo.ProductVersion
    if ((Get-Item -LiteralPath (Join-Path $SevenZipDir '7z.dll')).VersionInfo.ProductVersion -ne $sevenVersion) { throw "7-Zip: 7z.exe and 7z.dll in $SevenZipDir are different versions." }
    Write-Output "7-Zip $sevenVersion from $SevenZipDir"
}

# 1. The approved entry.
if (-not (Test-Path -LiteralPath $listFile)) { throw "dependencies.json is missing: $listFile" }
$list = [IO.File]::ReadAllText($listFile) | ConvertFrom-Json
$entry = $list.packages.slots_loader
if ($list.format -ne 1 -or $list.source -ne 'Wael3rd/SF6-ModSuite' -or -not $entry) { throw 'dependencies.json: expected format 1, source Wael3rd/SF6-ModSuite and a slots_loader entry.' }
if (-not $entry.version -or [string]$entry.release -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,99}$' -or [string]$entry.file -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}\.zip$' -or [string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not ([long]$entry.size -gt 0)) { throw 'dependencies.json: the slots_loader entry is malformed.' }
$pin = [ordered]@{ version = [string]$entry.version; release = [string]$entry.release; asset = [string]$entry.file; size = [long]$entry.size; sha256 = ([string]$entry.sha256).ToUpperInvariant(); host = [string]$list.source }
# 1.0.21: the Manager's slim zip (slots_loader.slim, made by the sync Action from the full zip) is bundled when the list has it.
$slim = $entry.slim
if ($slim) {
    if ([string]$slim.repository -ne $repo -or [string]$slim.release -notmatch '^slotsloader-v[A-Za-z0-9._+-]{1,90}$' -or [string]$slim.file -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}\.zip$' -or [string]$slim.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not ([long]$slim.size -gt 0) -or ([string]$slim.from_sha256).ToUpperInvariant() -ne $pin.sha256) { throw 'dependencies.json: slots_loader.slim is malformed or made from another zip.' }
    Write-Output ("Full zip of the entry: {0}, {1} bytes, SHA-256 {2} ({3} release {4})" -f $pin.asset, $pin.size, $pin.sha256, $list.source, $pin.release)
    $pin.release = [string]$slim.release; $pin.asset = [string]$slim.file; $pin.size = [long]$slim.size; $pin.sha256 = ([string]$slim.sha256).ToUpperInvariant(); $pin.host = $repo
}
$url = 'https://github.com/' + $pin.host + '/releases/download/' + [uri]::EscapeDataString($pin.release) + '/' + [uri]::EscapeDataString($pin.asset)
Write-Output ("Approved Slots Loader: {0} ({1} release {2}), {3}, {4} bytes, SHA-256 {5}" -f $pin.version, $pin.host, $pin.release, $pin.asset, $pin.size, $pin.sha256)

# 2. The zip, verified against the list.
$zipPath = Join-Path $cache $pin.asset
if ($LoaderZip) { Copy-Item -LiteralPath $LoaderZip -Destination $zipPath -Force }
elseif (-not (Test-Path -LiteralPath $zipPath)) {
    if ($NoLoaderFetch) { throw "-NoLoaderFetch needs -LoaderZip (the approved $($pin.asset))." }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
}
$size = (Get-Item -LiteralPath $zipPath).Length; $hash = Get-Sha256 $zipPath
if ($size -ne $pin.size -or $hash -ne $pin.sha256) { Remove-Item -LiteralPath $zipPath -Force; throw ("The approved hash in dependencies.json differs from the file: $($pin.asset) is $size bytes, SHA-256 $hash, but dependencies.json approves $($pin.size) bytes, SHA-256 $($pin.sha256). " + $(if ($size -eq $pin.size) { "Same size, other bytes: the release asset was probably re-uploaded under the same name; approve the new file (update dependencies.json) or use the approved one with -LoaderZip. " } else { "" }) + "Nothing was built.") }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$probe = [IO.Compression.ZipFile]::OpenRead($zipPath); $entries = $probe.Entries.Count; $probe.Dispose()
if ($entries -eq 0) { throw "$($pin.asset) is an empty zip." }
# 3. Build and pack.
$build = Join-Path $OutputDirectory 'build'
& (Join-Path $project 'build.ps1') -OutputDirectory $build
$exe = Join-Path $build 'Slotsloader Manager.exe'
$version = ([version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion).ToString(3)
$stage = Join-Path $OutputDirectory 'stage'; New-Item -ItemType Directory -Path (Join-Path $stage 'Slotsloader') -Force | Out-Null
foreach ($f in @('Slotsloader Manager.exe', 'Slotsloader Manager.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $build $f) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination $stage
Copy-Item -LiteralPath $zipPath -Destination (Join-Path $stage 'Slotsloader')
$validation = @("Slotsloader Manager $version", "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm') by tools\Package-Release.ps1.", "Bundled Slots Loader $($pin.version): $($pin.host) release $($pin.release), Slotsloader\$($pin.asset), $($pin.size) bytes, SHA-256 $($pin.sha256) (dependencies.json)")
if (-not $BundleSevenZip) { $validation += "7-Zip: not in this zip; the Manager downloads its pinned 7-Zip release tool-7zip-26.00 when no usable 7-Zip is installed." }
else {
    New-Item -ItemType Directory -Path (Join-Path $stage '7-Zip') -Force | Out-Null
    $validation += "Bundled 7-Zip $sevenVersion (x64, unmodified, GNU LGPL + unRAR restriction; see 7-Zip\License.txt; source at https://www.7-zip.org):"
    foreach ($f in $sevenFiles) {
        $to = Join-Path (Join-Path $stage '7-Zip') $f; Copy-Item -LiteralPath (Join-Path $SevenZipDir $f) -Destination $to
        $validation += ("  7-Zip\{0}, {1} bytes, SHA-256 {2}" -f $f, (Get-Item -LiteralPath $to).Length, (Get-Sha256 $to))
    }
}
if ($TestReport) { $validation += ''; $validation += 'Tests:'; $validation += Get-Content -LiteralPath $TestReport } else { $validation += 'Tests: none attached (run test.ps1 -Full -RealArchives and pass -TestReport before publishing).' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'VALIDATION.txt'), (($validation -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))

# Entries with forward slashes, the app files at the root, the loader under Slotsloader/ and 7-Zip under 7-Zip/.
$portable = Join-Path $OutputDirectory ("Slotsloader-Manager-$version-portable.zip")
$archive = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$sum = Get-Sha256 $portable
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), ("{0}  {1}`n" -f $sum.ToLowerInvariant(), (Split-Path -Leaf $portable)), (New-Object Text.UTF8Encoding($false)))
Write-Output "Packed $portable ($((Get-Item -LiteralPath $portable).Length) bytes, SHA-256 $sum) with Slots Loader $($pin.version)."
Write-Output "To publish (only when approved): gh release create manager-v$version --repo $repo --latest --title ""Slotsloader Manager $version"" ""$portable"" ""$(Join-Path $OutputDirectory 'SHA256SUMS.txt')"""
