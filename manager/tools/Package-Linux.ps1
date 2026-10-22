# Package an already built, tested Linux-enabled Manager. Never reads installed Data.
# Downloads and publishing are deliberately outside this packaging command.
param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$LoaderZip,
    [Parameter(Mandatory = $true)][string]$SevenZipZip,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$TestReport = '',
    [string]$Python = 'python',
    [string]$BetaLabel = 'linux-beta2'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
if ($BetaLabel -notmatch '^[a-zA-Z0-9.-]+$') { throw 'BetaLabel must contain only letters, numbers, dots or hyphens.' }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw 'Choose a new or empty output directory.' }
foreach ($name in @('Slotsloader Manager.exe', 'Slotsloader Manager.exe.config', 'Slotsloader CLI.exe', 'Slotsloader CLI.exe.config')) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuildDirectory $name) -PathType Leaf)) { throw "Missing build file: $name" }
}
$pin = Get-Content -LiteralPath (Join-Path $project 'assets\loader-pin.json') -Raw | ConvertFrom-Json
if ((Get-Item -LiteralPath $LoaderZip).Length -ne $pin.size -or (Get-FileHash -LiteralPath $LoaderZip -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'Loader archive does not match assets/loader-pin.json.' }
# This exact source pin is used by Manager's own verified download path.
$sevenSource = Get-Content -LiteralPath (Join-Path $project 'src\SevenZipTool.cs') -Raw
$sevenHash = [regex]::Match($sevenSource, 'Sha256 = "([A-Fa-f0-9]{64})"').Groups[1].Value
$sevenSize = [regex]::Match($sevenSource, 'Asset = "7-Zip-[^"]+", Size = (\d+)').Groups[1].Value
if (-not $sevenHash -or -not $sevenSize -or (Get-Item -LiteralPath $SevenZipZip).Length -ne [long]$sevenSize -or (Get-FileHash -LiteralPath $SevenZipZip -Algorithm SHA256).Hash -ne $sevenHash) { throw '7-Zip archive does not match the source release pin.' }
$version = ([version](Get-Item -LiteralPath (Join-Path $BuildDirectory 'Slotsloader Manager.exe')).VersionInfo.FileVersion).ToString(3)
foreach ($name in @('Inter-Regular.ttf', 'Inter-Bold.ttf', 'Inter-Italic.ttf', 'Inter-BoldItalic.ttf', 'OFL-Inter.txt', 'LiberationMono-Regular.ttf', 'LiberationMono-Bold.ttf', 'LiberationMono-Italic.ttf', 'LiberationMono-BoldItalic.ttf', 'OFL-Liberation.txt')) {
    $font = Join-Path (Join-Path $BuildDirectory 'Fonts') $name
    $sourceFont = Join-Path (Join-Path $project 'assets\fonts') $name
    if (-not (Test-Path -LiteralPath $font -PathType Leaf) -or (Get-FileHash -LiteralPath $font -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sourceFont -Algorithm SHA256).Hash) { throw "Missing or unverified Linux font: $name" }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $output "SlotsLoader-Manager-$version-$BetaLabel"
$app = Join-Path $stage 'app'
$linux = Join-Path $stage 'linux'
New-Item -ItemType Directory -Path $app, $linux, (Join-Path $app 'Slotsloader'), (Join-Path $app '7-Zip') -Force | Out-Null
foreach ($name in @('Slotsloader Manager.exe', 'Slotsloader Manager.exe.config', 'Slotsloader CLI.exe', 'Slotsloader CLI.exe.config')) { Copy-Item -LiteralPath (Join-Path $BuildDirectory $name) -Destination $app }
$packagedFonts = Join-Path $app 'Fonts'
New-Item -ItemType Directory -Path $packagedFonts -Force | Out-Null
foreach ($name in @('Inter-Regular.ttf', 'Inter-Bold.ttf', 'Inter-Italic.ttf', 'Inter-BoldItalic.ttf', 'OFL-Inter.txt', 'LiberationMono-Regular.ttf', 'LiberationMono-Bold.ttf', 'LiberationMono-Italic.ttf', 'LiberationMono-BoldItalic.ttf', 'OFL-Liberation.txt')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $BuildDirectory 'Fonts') $name) -Destination $packagedFonts
}
Copy-Item -LiteralPath $LoaderZip -Destination (Join-Path (Join-Path $app 'Slotsloader') $pin.asset)
foreach ($name in @('launcher.py', 'host.py', 'bottle.yml')) { Copy-Item -LiteralPath (Join-Path (Join-Path $project 'linux') $name) -Destination $linux }
Copy-Item -LiteralPath (Join-Path $project 'assets\slotsloader.ico') -Destination $linux
foreach ($name in @('install.sh', 'Install SlotsLoader Manager.desktop', 'README.md')) { Copy-Item -LiteralPath (Join-Path (Join-Path $project 'linux') $name) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $project 'THIRD_PARTY_NOTICES.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $project) 'LICENSE') -Destination (Join-Path $stage 'SLOTSLOADER-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $project) 'THIRD_PARTY.md') -Destination (Join-Path $stage 'SLOTSLOADER-THIRD-PARTY.md')
$utf8 = New-Object Text.UTF8Encoding($false)
$notices = @'
SlotsLoader Manager Linux compatibility package

Inter 4.1: unmodified Regular, Bold, Italic and Bold Italic fonts by the Inter
Project Authors, under the SIL Open Font License 1.1. See app/Fonts/OFL-Inter.txt.
Official source and release: https://github.com/rsms/inter/releases/tag/v4.1

Liberation Mono 2.1.5: unmodified Regular, Bold, Italic and Bold Italic fonts,
copyright Google Corporation and Red Hat, Inc., under the SIL Open Font License
1.1. See app/Fonts/OFL-Liberation.txt and the official source release:
https://github.com/liberationfonts/liberation-fonts/releases/tag/2.1.5

Manager, including its Linux launcher and helper: see LICENSE (personal-use
source-available terms) and THIRD_PARTY_NOTICES.md (embedded component notices).

7-Zip: unmodified official x64 binary distribution, pinned by SHA-256 in the source.
The bundled 7-Zip/License.txt contains the applicable LGPL and unRAR restrictions.
7-Zip source and notices: https://www.7-zip.org/

SlotsLoader: the exact pinned loader distribution is included under app/Slotsloader.
Its original archive and notices are preserved byte for byte.
See also SLOTSLOADER-LICENSE.txt and SLOTSLOADER-THIRD-PARTY.md; those files
describe the loader component, not the Manager's own code.

Bottles, Wine, Microsoft .NET Framework and Microsoft GDI+ are NOT redistributed
in this package. GDI+ is downloaded by Bottles under Microsoft's EULA and copied
privately into the installed app after verification; it is never in this archive.
Bottles downloads its runner and dependencies during graphical setup under their
own licences. This package invokes Bottles through its documented public CLI.
Bottles: https://usebottles.com/  Wine: https://www.winehq.org/

Linux launcher and host helper are supplied as readable Python source alongside
the application. See the repository licence and the included validation report.
'@
[IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $notices.Replace("`r`n", "`n") + "`n", $utf8)
if ($TestReport) { Copy-Item -LiteralPath $TestReport -Destination (Join-Path $stage 'VALIDATION.md') }
else { [IO.File]::WriteAllText((Join-Path $stage 'VALIDATION.md'), "# Experimental Linux package`n`nNo validation report was supplied. This archive is not a claim of Linux or Steam Deck runtime validation.`n", $utf8) }
$script = @'
import hashlib,json,pathlib,sys,tarfile,zipfile
stage=pathlib.Path(sys.argv[1]); seven=pathlib.Path(sys.argv[2]); version=sys.argv[3]
required={'7z.exe','7z.dll','License.txt'}
with zipfile.ZipFile(seven) as z:
    entries={}
    for member in z.infolist():
        if member.is_dir(): continue
        parts=pathlib.PurePosixPath(member.filename.replace('\\','/')).parts
        if '..' in parts or member.filename.startswith('/'):
            raise SystemExit('Unsafe 7-Zip archive member')
        if parts[-1] in required:
            if parts[-1] in entries: raise SystemExit('Duplicate 7-Zip archive member')
            entries[parts[-1]]=member
    if set(entries)!=required: raise SystemExit('7-Zip release is incomplete')
    for name,member in entries.items(): (stage/'app'/'7-Zip'/name).write_bytes(z.read(member))
files={}
for p in sorted(stage.rglob('*')):
    if not p.is_file(): continue
    name=p.relative_to(stage).as_posix()
    if p.name.casefold()=='gdiplus.dll': raise SystemExit('Microsoft GDI+ must be downloaded by Bottles, never packaged')
    if any(part.casefold()=='data' for part in p.relative_to(stage).parts): raise SystemExit('User Data must never be packaged')
    if not name.startswith('app/') and p.suffix in ('.py','.sh','.desktop','.yml','.md','.txt'):
        p.write_bytes(p.read_bytes().replace(b'\r\n',b'\n'))
    files[name]=hashlib.sha256(p.read_bytes()).hexdigest()
(stage/'package.json').write_text(json.dumps({'format':1,'version':version,'files':files},indent=2)+'\n',encoding='utf-8')
archive=stage.parent/(stage.name+'.tar.gz')
def metadata(info):
    info.uid=info.gid=0; info.uname=info.gname=''; info.mtime=0
    info.mode=0o755 if info.isdir() or info.name.endswith(('.sh','.desktop')) else 0o644
    return info
with tarfile.open(archive,'w:gz',format=tarfile.PAX_FORMAT) as tar:
    tar.add(stage,arcname=stage.name,filter=metadata)
with tarfile.open(archive) as tar:
    members=tar.getmembers()
    if any(m.issym() or m.islnk() for m in members): raise SystemExit('Archive must not contain links')
    for m in members:
        if m.name.endswith(('.sh','.desktop')) and not m.mode & 0o111: raise SystemExit('Launcher executable bit missing')
digest=hashlib.sha256(archive.read_bytes()).hexdigest()
(archive.parent/'SHA256SUMS.txt').write_text(digest+'  '+archive.name+'\n',encoding='utf-8')
print(str(archive)); print('SHA-256 '+digest)
'@
$helper = Join-Path $output 'package-linux.py'
[IO.File]::WriteAllText($helper, $script.Replace("`r`n", "`n"), $utf8)
& $Python $helper $stage $SevenZipZip "$version-$BetaLabel"
if ($LASTEXITCODE -ne 0) { throw 'Linux packaging or archive verification failed.' }
Write-Output 'Package created locally. No release was published and no installed Data was read.'
