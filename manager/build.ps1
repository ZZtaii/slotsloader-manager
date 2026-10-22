param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSCommandPath
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'out\build' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Linux uses the same app-local font in GDI and GDI+; Windows retains Segoe UI.
$fontOutput = Join-Path $OutputDirectory 'Fonts'
New-Item -ItemType Directory -Path $fontOutput -Force | Out-Null
foreach ($name in @('Inter-Regular.ttf', 'Inter-Bold.ttf', 'Inter-Italic.ttf', 'Inter-BoldItalic.ttf', 'OFL-Inter.txt', 'LiberationMono-Regular.ttf', 'LiberationMono-Bold.ttf', 'LiberationMono-Italic.ttf', 'LiberationMono-BoldItalic.ttf', 'OFL-Liberation.txt')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $taskRoot 'assets\fonts') $name) -Destination $fontOutput -Force
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$target = Join-Path $OutputDirectory 'Slotsloader Manager.exe'
$appIcon = Join-Path $taskRoot 'assets\slotsloader.ico'
# 2.5.9: the Slotsloader release the app installs (tools\Package-Release.ps1 rewrites it with the newest one).
$loaderPin = Join-Path $taskRoot 'assets\loader-pin.json'
# 1.0.11: the BrewedVFX logo on the Settings → About credit card.
$brewedLogo = Join-Path $taskRoot 'assets\brewedvfx.png'
# 1.0.11: the in-app Help (gear → Help, F1), read by HelpDocument.cs.
$help = Join-Path $taskRoot 'docs\HELP.md'
# 2.4.0: the Assistant's knowledge pack, generated from the user-facing docs (deterministic; fails on excluded text).
$knowledge = Join-Path $taskRoot 'obj\assistant-knowledge.md'
& (Join-Path $taskRoot 'tools\Build-AssistantKnowledge.ps1') -Project $taskRoot -Output $knowledge
if (-not $?) { throw 'Assistant knowledge pack failed.' }
# 2.4.3: no personal or local paths in shipping source, docs or the knowledge pack.
& (Join-Path $taskRoot 'tools\Check-PublicPaths.ps1') -Project $taskRoot -Extra @($knowledge)
# 2.5.1: no other product named in the user docs or the knowledge pack.
& (Join-Path $taskRoot 'tools\Check-Wording.ps1') -Project $taskRoot -Extra @($knowledge)
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
& $compiler /nologo /target:winexe /main:SlotsInstaller.Program /platform:x64 /optimize+ "/out:$target" "/win32manifest:$(Join-Path $taskRoot 'src\app.manifest')" "/win32icon:$appIcon" "/resource:$appIcon,SlotsInstaller.AppIcon" "/resource:$knowledge,SlotsInstaller.AssistantKnowledge.md" "/resource:$loaderPin,SlotsInstaller.LoaderPin.json" "/resource:$brewedLogo,SlotsInstaller.BrewedVfx.png" "/resource:$help,SlotsInstaller.Help.md" /r:Accessibility.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll /r:System.Security.dll /r:Microsoft.VisualBasic.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\app.config') -Destination ($target + '.config') -Force
Write-Output "Built $target"
$cliTarget = Join-Path $OutputDirectory 'Slotsloader CLI.exe'
& $compiler /nologo /target:exe /define:CLI /main:SlotsInstaller.CommandLineProgram /platform:x64 /optimize+ "/out:$cliTarget" "/win32manifest:$(Join-Path $taskRoot 'src\app.manifest')" "/win32icon:$appIcon" "/resource:$appIcon,SlotsInstaller.AppIcon" "/resource:$knowledge,SlotsInstaller.AssistantKnowledge.md" "/resource:$loaderPin,SlotsInstaller.LoaderPin.json" "/resource:$brewedLogo,SlotsInstaller.BrewedVfx.png" "/resource:$help,SlotsInstaller.Help.md" /r:Accessibility.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Net.Http.dll /r:System.Security.dll /r:Microsoft.VisualBasic.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'CLI compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\app.config') -Destination ($cliTarget + '.config') -Force
Write-Output "Built $cliTarget"
