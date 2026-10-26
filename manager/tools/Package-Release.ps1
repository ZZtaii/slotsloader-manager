# 2.5.9: builds the Manager release zip with the newest published Slotsloader inside it.
#  1. Asks GitHub (gh, signed in) for the newest non-draft, non-pre-release loader-v* release of ZZtaii/slotsloader-manager,
#     with exactly one .zip asset and GitHub's sha256 digest. Refuses a release older than the current pin unless
#     -AllowOlderLoader (so a Manager never ships an older loader than the one it already pins).
#  2. Downloads that zip, checks its size and SHA-256 against the digest, and rewrites assets\loader-pin.json.
#  3. Builds (build.ps1 embeds the pin) and packs Slotsloader-Manager-<version>-portable.zip: the Manager exe, its
#     config and README.md at the root (1.0.13 also the CLI, its config, CLI.md and VALIDATION.txt) plus
#     Slotsloader\<loader zip>, then SHA256SUMS.txt beside it.
#  4. 1.0.11: 7-Zip is NOT in the zip: a Manager 1.0.10 or older refuses an update zip with a 7-Zip folder ("unexpected
#     files"), and someone can update from any older version. The Manager fetches its pinned 7-Zip release
#     (SevenZipTool.Supported, tag tool-7zip-26.00) when no usable 7-Zip is installed. -BundleSevenZip still puts
#     7-Zip\7z.exe, 7z.dll and License.txt in (x64, unmodified, from -SevenZipDir, default: the installed 7-Zip), for
#     a zip that is never offered as an update.
# Nothing is published: the commands to do that are printed at the end.
# -LoaderZip <path>: use this local zip instead of downloading (it must match the pin, or the newest release's
#  digest when fetching). -NoLoaderFetch: keep assets\loader-pin.json as it is (offline); needs -LoaderZip or a
#  cached copy in <output>\loader-cache.
param([string]$OutputDirectory = '', [switch]$NoLoaderFetch, [switch]$AllowOlderLoader, [string]$LoaderZip = '', [string]$TestReport = '', [string]$SevenZipDir = '', [switch]$BundleSevenZip)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$repo = 'ZZtaii/slotsloader-manager'
$pinFile = Join-Path $project 'assets\loader-pin.json'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project ('out\release-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw "Choose a new or empty output directory: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cache = Join-Path $OutputDirectory 'loader-cache'; New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Get-LoaderVersion([string]$text) {
    $m = [regex]::Match($text, '(\d+)\.(\d+)(?:\.(\d+))?')
    if (-not $m.Success) { throw "No version in '$text'." }
    $build = if ($m.Groups[3].Success) { $m.Groups[3].Value } else { '0' }
    return [version]('{0}.{1}.{2}' -f $m.Groups[1].Value, $m.Groups[2].Value, $build)
}
function Get-Sha256([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Write-Pin($pin) {
    $text = "{`n  ""tag"": ""$($pin.tag)"",`n  ""version"": ""$($pin.version)"",`n  ""asset"": ""$($pin.asset)"",`n  ""size"": $($pin.size),`n  ""sha256"": ""$($pin.sha256)""`n}`n"
    [IO.File]::WriteAllText($pinFile, $text, (New-Object Text.UTF8Encoding($false)))
}

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

$current = Get-Content -LiteralPath $pinFile -Raw | ConvertFrom-Json
$pin = [ordered]@{ tag = $current.tag; version = $current.version; asset = $current.asset; size = [long]$current.size; sha256 = ([string]$current.sha256).ToUpperInvariant() }

# 1. The newest published loader release.
if (-not $NoLoaderFetch) {
    $json = (& gh api "repos/$repo/releases?per_page=100") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "gh api failed (signed in with access to $repo?)." }
    $all = ConvertFrom-Json $json   # a variable, so the array is enumerated (PowerShell 5.1 pipes it as one item)
    $loaders = @($all | Where-Object { $_.tag_name -like 'loader-v*' -and -not $_.draft -and -not $_.prerelease } | Sort-Object { Get-LoaderVersion $_.tag_name.Substring(8) } -Descending)
    if ($loaders.Count -eq 0) { throw "No published loader-v* release on $repo." }
    $newest = $loaders[0]
    $zips = @($newest.assets | Where-Object { $_.name -like '*.zip' })
    if ($zips.Count -ne 1) { throw "$($newest.tag_name) has $($zips.Count) .zip assets (expected one)." }
    $asset = $zips[0]
    if ([string]$asset.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw "$($newest.tag_name): GitHub shows no sha256 digest for $($asset.name)." }
    $digest = $Matches[1].ToUpperInvariant()
    $newestVersion = Get-LoaderVersion $newest.tag_name.Substring(8)
    if ($newestVersion -lt (Get-LoaderVersion $pin.version) -and -not $AllowOlderLoader) {
        throw "The newest published Slotsloader is $($newest.tag_name), older than the current pin $($pin.tag). Publish $($pin.tag) (or newer) first, or pass -AllowOlderLoader."
    }
    $pin = [ordered]@{ tag = $newest.tag_name; version = $newest.tag_name.Substring(8); asset = $asset.name; size = [long]$asset.size; sha256 = $digest }
    Write-Output ("Newest Slotsloader release: {0}, {1}, {2} bytes, SHA-256 {3}" -f $pin.tag, $pin.asset, $pin.size, $pin.sha256)
}

# 2. The zip, verified against the pin.
$zipPath = Join-Path $cache $pin.asset
if ($LoaderZip) { Copy-Item -LiteralPath $LoaderZip -Destination $zipPath -Force }
elseif (-not (Test-Path -LiteralPath $zipPath)) {
    if ($NoLoaderFetch) { throw "-NoLoaderFetch needs -LoaderZip (the pinned $($pin.asset))." }
    & gh release download $pin.tag --repo $repo --pattern $pin.asset --dir $cache --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release download $($pin.tag) failed." }
}
$size = (Get-Item -LiteralPath $zipPath).Length; $hash = Get-Sha256 $zipPath
if ($size -ne $pin.size -or $hash -ne $pin.sha256) { throw "$($pin.asset) is $size bytes, SHA-256 $hash; the pin says $($pin.size) bytes, $($pin.sha256). Nothing was built." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$probe = [IO.Compression.ZipFile]::OpenRead($zipPath); $entries = $probe.Entries.Count; $probe.Dispose()
if ($entries -eq 0) { throw "$($pin.asset) is an empty zip." }
if ($pin.tag -ne $current.tag -or $pin.sha256 -ne ([string]$current.sha256).ToUpperInvariant()) { Write-Pin $pin; Write-Output "assets\loader-pin.json now pins $($pin.tag) (was $($current.tag))." }
else { Write-Output "assets\loader-pin.json already pins $($pin.tag)." }

# 3. Build and pack.
$build = Join-Path $OutputDirectory 'build'
& (Join-Path $project 'build.ps1') -OutputDirectory $build
$exe = Join-Path $build 'Slotsloader Manager.exe'
$version = ([version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion).ToString(3)
$stage = Join-Path $OutputDirectory 'stage'; New-Item -ItemType Directory -Path (Join-Path $stage 'Slotsloader') -Force | Out-Null
# 1.0.13: the release is the Manager, its config, README and Slotsloader\. The CLI is still built (build\) but not
# shipped, nor CLI.md or VALIDATION.txt (VALIDATION.txt goes beside the zip). A Manager 1.0.12 or older updates only
# from a zip with all 7 files, so 1.0.13 (the first updater that accepts fewer) still carries them.
$compatible = [version]$version -lt [version]'1.0.14'
$files = @('Slotsloader Manager.exe', 'Slotsloader Manager.exe.config'); if ($compatible) { $files += 'Slotsloader CLI.exe', 'Slotsloader CLI.exe.config' }
foreach ($f in $files) { Copy-Item -LiteralPath (Join-Path $build $f) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination $stage
if ($compatible) { Copy-Item -LiteralPath (Join-Path $project 'docs\CLI.md') -Destination $stage; Write-Output "$version still carries the CLI, CLI.md and VALIDATION.txt so Manager 1.0.12 and older can update to it." }
Copy-Item -LiteralPath $zipPath -Destination (Join-Path $stage 'Slotsloader')
$validation = @("Slotsloader Manager $version", "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm') by tools\Package-Release.ps1.", "Bundled Slotsloader: $($pin.tag), Slotsloader\$($pin.asset), $($pin.size) bytes, SHA-256 $($pin.sha256)")
if (-not $BundleSevenZip) { $validation += "7-Zip: not in this zip (any older Manager can update to it); the Manager downloads its pinned 7-Zip release tool-7zip-26.00 when no usable 7-Zip is installed." }
else {
    New-Item -ItemType Directory -Path (Join-Path $stage '7-Zip') -Force | Out-Null
    $validation += "Bundled 7-Zip $sevenVersion (x64, unmodified, GNU LGPL + unRAR restriction; see 7-Zip\License.txt; source at https://www.7-zip.org):"
    foreach ($f in $sevenFiles) {
        $to = Join-Path (Join-Path $stage '7-Zip') $f; Copy-Item -LiteralPath (Join-Path $SevenZipDir $f) -Destination $to
        $validation += ("  7-Zip\{0}, {1} bytes, SHA-256 {2}" -f $f, (Get-Item -LiteralPath $to).Length, (Get-Sha256 $to))
    }
}
if ($TestReport) { $validation += ''; $validation += 'Tests:'; $validation += Get-Content -LiteralPath $TestReport } else { $validation += 'Tests: none attached (run test.ps1 -RealArchives and pass -TestReport before publishing).' }
[IO.File]::WriteAllText((Join-Path $(if ($compatible) { $stage } else { $OutputDirectory }) 'VALIDATION.txt'), (($validation -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))

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
Write-Output "Packed $portable ($((Get-Item -LiteralPath $portable).Length) bytes, SHA-256 $sum) with $($pin.tag)."
Write-Output "To publish (only when approved): gh release create manager-v$version --repo $repo --latest --title ""Slotsloader Manager $version"" ""$portable"" ""$(Join-Path $OutputDirectory 'SHA256SUMS.txt')"""
