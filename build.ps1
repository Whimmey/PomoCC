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

# %VERSION% in the output name is filled from src\AssemblyInfo.cs (AssemblyVersion).
# Single source of truth: the version in the file name and the version inside the exe
# can never drift apart, and a release is just "bump AssemblyInfo + build".
if ($exeName.Contains('%VERSION%')) {
  $asmInfo = Join-Path $srcDir 'AssemblyInfo.cs'
  $ver = $null
  if (Test-Path $asmInfo) {
    $m = Select-String -Path $asmInfo -Pattern 'AssemblyVersion\("([0-9]+)\.([0-9]+)\.([0-9]+)' | Select-Object -First 1
    if ($m) {
      $ver = $m.Matches[0].Groups[1].Value + '.' + $m.Matches[0].Groups[2].Value + '.' + $m.Matches[0].Groups[3].Value
    }
  }
  if (-not $ver) {
    throw "exe-name.txt uses %VERSION% but no AssemblyVersion was found in src\AssemblyInfo.cs"
  }
  $exeName = $exeName.Replace('%VERSION%', $ver)
  Write-Output ("Version substituted into output name: v" + $ver)
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

$cscArgs = @(
  '/nologo',
  '/target:winexe',
  '/platform:anycpu',
  '/optimize+',
  '/codepage:65001',
  ('/win32manifest:' + $manifest),
  ('/out:' + $exe)
) + $refs + $files

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compile failed with exit code $LASTEXITCODE" }

# exe.config must sit next to the exe with the exact same name; the WinForms high-DPI switch lives there.
Copy-Item $appConfig ($exe + '.config') -Force

# Ship the user manual next to the exe. Source of truth is docs\ (dist\ is build output only),
# so the file name is discovered instead of hard-coded here - this script must stay pure ASCII.
$docsDir = Join-Path $root 'docs'
if (Test-Path $docsDir) {
  $manual = Get-ChildItem -Path $docsDir -Filter '*.txt' -File | Select-Object -First 1
  if ($manual) {
    Copy-Item $manual.FullName (Join-Path $distDir $manual.Name) -Force
    Write-Output ("User manual copied: " + $manual.Name)
  }
}

$item = Get-Item $exe
Write-Output ("Built: " + $item.FullName)
Write-Output ("Size : " + [math]::Round($item.Length / 1KB, 1) + " KB")
Write-Output ("DPI manifest embedded; exe.config written: " + (Test-Path ($exe + '.config')))
