# Build a single self-contained exe with the .NET Framework compiler that ships with Windows.
# NOTE: keep this file pure ASCII. Windows PowerShell decodes a BOM-less script as ANSI,
#       which would mangle any non-ASCII literal. The Chinese exe name lives in exe-name.txt
#       (read back with an explicit UTF-8 decode) and may contain %VERSION%, which is
#       replaced with the AssemblyVersion parsed out of src\AssemblyInfo.cs.
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir  = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$nameFile = Join-Path $root 'exe-name.txt'
if (-not (Test-Path $nameFile)) { throw "exe-name.txt not found: $nameFile" }
$exeName = (Get-Content -Path $nameFile -Encoding UTF8 | Select-Object -First 1).Trim()
if ([string]::IsNullOrEmpty($exeName)) { throw "exe-name.txt is empty" }

# Allow a temporary output name (used when the real exe is locked by another program).
if (-not [string]::IsNullOrEmpty($env:PS_EXE_NAME)) {
  $exeName = $env:PS_EXE_NAME
  Write-Output ("Output name overridden via PS_EXE_NAME: " + $exeName)
}

# Read the release version once. It is used by the optional exe name placeholder and
# by the release zip, so both always agree with AssemblyVersion.
$asmInfo = Join-Path $srcDir 'AssemblyInfo.cs'
if (-not (Test-Path $asmInfo)) { throw "AssemblyInfo.cs not found: $asmInfo" }
$versionMatch = Select-String -Path $asmInfo -Pattern 'AssemblyVersion\("([0-9]+)\.([0-9]+)\.([0-9]+)' | Select-Object -First 1
if (-not $versionMatch) { throw "AssemblyVersion was not found in src\AssemblyInfo.cs" }
$releaseVersion = $versionMatch.Matches[0].Groups[1].Value + '.' +
                  $versionMatch.Matches[0].Groups[2].Value + '.' +
                  $versionMatch.Matches[0].Groups[3].Value

# Explorer shows the *informational* version as "product version", and the app shows the same
# string in its own footer. The uninstaller must carry that string too - deriving it from the
# 3-part AssemblyVersion instead is exactly how the two exes ended up showing 0.2 and 0.2.0.
$infoMatch = Select-String -Path $asmInfo -Pattern 'AssemblyInformationalVersion\("([^"]+)"' | Select-Object -First 1
$informationalVersion = if ($infoMatch) { $infoMatch.Matches[0].Groups[1].Value } else { $releaseVersion }

if ($exeName.Contains('%VERSION%')) {
  $exeName = $exeName.Replace('%VERSION%', $releaseVersion)
  Write-Output ("Version substituted into output name: v" + $releaseVersion)
}

$refNames = @(
  'System.dll',
  'System.Core.dll',
  'System.Drawing.dll',
  'System.Windows.Forms.dll',
  'System.Web.Extensions.dll',
  'System.Security.dll'
)
$refs = @()
foreach ($n in $refNames) {
  $p = Join-Path $fw $n
  if (-not (Test-Path $p)) { throw "Missing reference assembly: $p" }
  $refs += ('/r:' + $p)
}

$files = @(Get-ChildItem -Path (Join-Path $srcDir '*.cs') | ForEach-Object { $_.FullName })
if ($files.Count -eq 0) { throw "No .cs files found in src" }

$exe      = Join-Path $distDir $exeName
$manifest = Join-Path $srcDir 'app.manifest'
$appConfig = Join-Path $srcDir 'app.config'
if (-not (Test-Path $manifest))  { throw "Missing DPI manifest: $manifest" }
if (-not (Test-Path $appConfig)) { throw "Missing app.config: $appConfig" }

# Generate the multi-size icon in the temp directory; it is embedded into the exes
# and never copied into dist as a separate release file.
$iconSource = Join-Path $root 'tools\IconGenerator.cs'
$iconArtSource = Join-Path $srcDir 'IconArt.cs'
if (-not (Test-Path $iconSource)) { throw "Missing icon generator source: $iconSource" }
if (-not (Test-Path $iconArtSource)) { throw "Missing icon art source: $iconArtSource" }
$iconGeneratorExe = Join-Path ([IO.Path]::GetTempPath()) ('PomoCC-icon-' + [guid]::NewGuid().ToString('N') + '.exe')
$iconPath = Join-Path ([IO.Path]::GetTempPath()) ('PomoCC-icon-' + [guid]::NewGuid().ToString('N') + '.ico')
$iconGeneratorArgs = @(
  '/nologo',
  '/target:exe',
  '/platform:anycpu',
  '/optimize+',
  '/codepage:65001',
  ('/out:' + $iconGeneratorExe)
) + @('/r:' + (Join-Path $fw 'System.Drawing.dll')) + @($iconSource, $iconArtSource)

try {
  & $csc $iconGeneratorArgs
  if ($LASTEXITCODE -ne 0) { throw "Icon generator compile failed with exit code $LASTEXITCODE" }
  & $iconGeneratorExe $iconPath
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path $iconPath)) { throw "Icon generation failed" }
} catch {
  # Generation failed: the half-written ico is useless, so drop it too.
  Remove-Item -LiteralPath $iconPath -Force -ErrorAction SilentlyContinue
  throw
} finally {
  # Also runs when the icon self-check fails, so a failed build leaves no temp files behind.
  Remove-Item -LiteralPath $iconGeneratorExe -Force -ErrorAction SilentlyContinue
}

$cscArgs = @(
  '/nologo',
  '/target:winexe',
  '/platform:anycpu',
  '/optimize+',
  '/codepage:65001',
  ('/win32manifest:' + $manifest),
  ('/win32icon:' + $iconPath),
  ('/out:' + $exe)
) + $refs + $files

$uninstallerExe = Join-Path $distDir 'uninstall.exe'
$uninstallerInfo = $null
try {
  & $csc $cscArgs
  if ($LASTEXITCODE -ne 0) { throw "Compile failed with exit code $LASTEXITCODE" }

  # exe.config must sit next to the exe with the exact same name; the WinForms high-DPI switch lives there.
  Copy-Item $appConfig ($exe + '.config') -Force

  # Build the standalone uninstaller separately so its Main() never enters the app build.
  $uninstallerSource = Join-Path $root 'uninstaller\Program.cs'
  if (-not (Test-Path $uninstallerSource)) { throw "Missing uninstaller source: $uninstallerSource" }

  # The uninstaller carries the same version/product metadata as the app so Explorer shows it.
  # The template keeps the Chinese text out of this ASCII-only script; %VERSION% (3 parts, used
  # for AssemblyVersion/FileVersion) and %INFORMATIONAL% (used for the displayed product version)
  # both come from src\AssemblyInfo.cs, so the two exes can never disagree.
  $uninstallerInfoTemplate = Join-Path $root 'uninstaller\AssemblyInfo.cs.template'
  if (-not (Test-Path $uninstallerInfoTemplate)) { throw "Missing uninstaller info template: $uninstallerInfoTemplate" }
  $uninstallerInfo = Join-Path ([IO.Path]::GetTempPath()) ('PomoCC-uninstaller-info-' + [guid]::NewGuid().ToString('N') + '.cs')
  $uninstallerInfoText = (Get-Content -LiteralPath $uninstallerInfoTemplate -Encoding UTF8 -Raw).Replace('%VERSION%', $releaseVersion).Replace('%INFORMATIONAL%', $informationalVersion)
  [IO.File]::WriteAllText($uninstallerInfo, $uninstallerInfoText, (New-Object Text.UTF8Encoding($false)))

  $uninstallerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    ('/win32manifest:' + $manifest),
    ('/win32icon:' + $iconPath),
    ('/out:' + $uninstallerExe)
  ) + $refs + @($uninstallerSource, $uninstallerInfo)

  & $csc $uninstallerArgs
  if ($LASTEXITCODE -ne 0) { throw "Uninstaller compile failed with exit code $LASTEXITCODE" }
} finally {
  Remove-Item -LiteralPath $iconPath -Force -ErrorAction SilentlyContinue
  if (-not [string]::IsNullOrEmpty($uninstallerInfo)) {
    Remove-Item -LiteralPath $uninstallerInfo -Force -ErrorAction SilentlyContinue
  }
}

# Ship the user manual next to the exe. Source of truth is docs\ (dist\ is build output only),
# so the file name is discovered instead of hard-coded here - this script must stay pure ASCII.
$docsDir = Join-Path $root 'docs'
if (-not (Test-Path $docsDir)) { throw "Docs directory not found: $docsDir" }
$manual = Get-ChildItem -Path $docsDir -Filter '*.txt' -File | Select-Object -First 1
if (-not $manual) { throw "User manual was not found in docs" }
$manualOutput = Join-Path $distDir $manual.Name
Copy-Item $manual.FullName $manualOutput -Force
Write-Output ("User manual copied: " + $manual.Name)

# Package exactly the four portable release files at the root of the archive.
$releaseDir = Join-Path $root 'release'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$zipPath = Join-Path $releaseDir ("PomoCC-v" + $releaseVersion + ".zip")
$releaseFiles = @($exe, ($exe + '.config'), $uninstallerExe, $manualOutput)
foreach ($releaseFile in $releaseFiles) {
  if (-not (Test-Path -LiteralPath $releaseFile -PathType Leaf)) {
    throw "Release file missing: $releaseFile"
  }
}

Compress-Archive -LiteralPath $releaseFiles -DestinationPath $zipPath -Force

# Refuse to report success if the archive contains a missing, extra, or nested entry.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
  $actualEntries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
} finally {
  $archive.Dispose()
}
$expectedEntries = @($releaseFiles | ForEach-Object { Split-Path -Leaf $_ } | Sort-Object)
$entryDiff = @(Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries)
if ($entryDiff.Count -ne 0 -or $actualEntries.Count -ne 4) {
  throw "Release zip validation failed: expected exactly four root files"
}

$item = Get-Item $exe
Write-Output ("Built: " + $item.FullName)
Write-Output ("Size : " + [math]::Round($item.Length / 1KB, 1) + " KB")
Write-Output ("DPI manifest embedded; exe.config written: " + (Test-Path ($exe + '.config')))
Write-Output ("Uninstaller built: " + $uninstallerExe)
Write-Output ("Release package: " + $zipPath)
