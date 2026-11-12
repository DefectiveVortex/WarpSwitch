<#
.SYNOPSIS
  Builds WARP Switch: fetches and verifies usque + wintun, builds the app, runs the tests, and writes the
  portable exe, the installer and SHA256SUMS.txt to dist\.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1                   # version from WarpSwitch.csproj
  powershell -ExecutionPolicy Bypass -File build.ps1 -Version 2.2.0    # what the release workflow runs

.NOTES
  Needs the .NET SDK (8 or later) and, for the installer, Inno Setup 6 (ISCC.exe on PATH, in its default
  folder, or in $env:ISCC). Nothing else: .NET Framework 4.8 ships with Windows.
#>
param(
  [string]$Version,
  [switch]$SkipTests,
  [switch]$SkipInstaller,
  [switch]$Sign   # local builds only: sign with a self-made "CN=Vortex" certificate from your certificate store
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = $PSScriptRoot
$deps = Join-Path $root '.deps'
$res  = Join-Path $root 'res'
$dist = Join-Path $root 'dist'
foreach ($d in $deps, $res, $dist) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }

if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'WarpSwitch.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 2.1.0 (got '$Version')" }
"WARP Switch $Version"

function Sha256([string]$path) {
  $s = [IO.File]::OpenRead($path)
  try { -join ([Security.Cryptography.SHA256]::Create().ComputeHash($s) | ForEach-Object { $_.ToString('X2') }) } finally { $s.Dispose() }
}

# --- 1. third-party helpers, pinned by SHA-256 -------------------------------------------------------------
# usque: MASQUE client for WARP (MIT). Hash from the release's own checksums.txt.
$usque  = @{ Url = 'https://github.com/Diniboy1123/usque/releases/download/v4.2.1/usque_4.2.1_windows_amd64.zip'
             Zip = 'F6F7F0A1A2BC9BCC15CF563EC1F892D00690A92C086B23ED3211B802209099E7'
             Entry = 'usque.exe'; Exe = '122F2E40208E81BB814FCB4BE4FBBBDC9246860F1F3784F72AE4512B97667E91' }
# wintun: WireGuard's signed TUN driver (redistributable alongside software that uses its API).
$wintun = @{ Url = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
             Zip = '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51'
             Entry = 'wintun/bin/amd64/wintun.dll'; Exe = 'E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE' }

function Fetch($dep) {
  $zip = Join-Path $deps ([IO.Path]::GetFileName($dep.Url))
  if (-not (Test-Path $zip) -or (Sha256 $zip) -ne $dep.Zip) {
    Write-Host "  downloading $($dep.Url)"
    Invoke-WebRequest -Uri $dep.Url -OutFile $zip -UseBasicParsing
  }
  if ((Sha256 $zip) -ne $dep.Zip) { Remove-Item $zip; throw "checksum mismatch: $zip" }
  $z = [IO.Compression.ZipFile]::OpenRead($zip)
  try {
    $entry = $z.Entries | Where-Object { $_.FullName -eq $dep.Entry } | Select-Object -First 1
    if (-not $entry) { throw "$($dep.Entry) not found in $zip" }
    $out = Join-Path $deps ([IO.Path]::GetFileName($dep.Entry))
    $s = $entry.Open(); $f = [IO.File]::Create($out)
    try { $s.CopyTo($f) } finally { $f.Dispose(); $s.Dispose() }
  } finally { $z.Dispose() }
  if ((Sha256 $out) -ne $dep.Exe) { throw "checksum mismatch: $out" }
  $out
}

function GzipTo([string]$src, [string]$dest) {
  $in = [IO.File]::OpenRead($src); $o = [IO.File]::Create($dest)
  $gz = New-Object IO.Compression.GZipStream($o, [IO.Compression.CompressionLevel]::Optimal)
  try { $in.CopyTo($gz) } finally { $gz.Dispose(); $o.Dispose(); $in.Dispose() }
}

'1/5 helpers'
GzipTo (Fetch $usque) (Join-Path $res 'usque.exe.gz')
GzipTo (Fetch $wintun) (Join-Path $res 'wintun.dll.gz')

# --- 2. app icon (the Vortex mark: sunset-gradient disc with a white three-arm swirl), only if missing ------
$ico = Join-Path $res 'app.ico'
if (-not (Test-Path $ico)) {
  Add-Type -AssemblyName System.Drawing
  function IconFrame([int]$n) {
    $bmp = New-Object Drawing.Bitmap $n, $n, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $pad = [Math]::Max(0.5, $n * 0.03); $d = $n - 2 * $pad
    $rect = New-Object Drawing.RectangleF $pad, $pad, $d, $d
    $lg = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::White), ([Drawing.Color]::Black), 50
    $blend = New-Object Drawing.Drawing2D.ColorBlend 4
    $blend.Colors = @([Drawing.Color]::FromArgb(255,255,179,71), [Drawing.Color]::FromArgb(255,255,138,31), [Drawing.Color]::FromArgb(255,255,61,127), [Drawing.Color]::FromArgb(255,139,92,246))
    $blend.Positions = [single[]]@(0, 0.35, 0.72, 1)
    $lg.InterpolationColors = $blend
    $g.FillEllipse($lg, $rect)
    if ($n -ge 32) {
      $hl = New-Object Drawing.RectangleF ($pad + $d * 0.16), ($pad + $d * 0.05), ($d * 0.68), ($d * 0.45)
      $hb = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(38, 255, 255, 255)); $g.FillEllipse($hb, $hl); $hb.Dispose()
    }
    $w = [Math]::Max(1.3, $n * 0.085); $rad = $n * 0.34; $cx = $n / 2; $cy = $n / 2
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    for ($arm = 0; $arm -lt 3; $arm++) {
      $pts = New-Object 'Drawing.PointF[]' 10
      for ($i = 0; $i -lt 10; $i++) {
        $t = $i / 9; $rr = $rad * (0.22 + 0.78 * $t); $th = $arm * 2 * [Math]::PI / 3 + 2.3 * $t
        $pts[$i] = New-Object Drawing.PointF ($cx + [Math]::Cos($th) * $rr), ($cy + [Math]::Sin($th) * $rr)
      }
      $g.DrawCurve($pen, $pts, 0.5)
    }
    $g.Dispose(); $lg.Dispose(); $pen.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
  }
  $sizes = 16, 20, 24, 32, 40, 48, 64, 256
  $frames = foreach ($n in $sizes) { , (IconFrame $n) }
  $mem = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $mem
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($n % 256)); $bw.Write([byte]($n % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
  }
  foreach ($f in $frames) { $bw.Write($f) }
  [IO.File]::WriteAllBytes($ico, $mem.ToArray())
}

# --- 3. build + test ------------------------------------------------------------------------------------------
'2/5 build'
Push-Location $root
try {
  & dotnet build WarpSwitch.csproj -c Release -nologo -v q "-p:Version=$Version"
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
  if (-not $SkipTests) {
    '3/5 tests'
    & dotnet test tests\WarpSwitch.Tests -c Release -nologo -v q "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
  } else { '3/5 tests skipped' }
} finally { Pop-Location }
$exe = Join-Path $root 'bin\Release\net48\WarpSwitch.exe'

function SignFile([string]$path) {
  $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=Vortex' -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
          Sort-Object NotAfter -Descending | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Vortex' -FriendlyName 'Vortex code signing' -CertStoreLocation Cert:\CurrentUser\My `
            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
  }
  $sig = $null
  try { $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction Stop } catch { }
  if (-not $sig -or -not $sig.SignerCertificate) { $sig = Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256 }
  "  signed $([IO.Path]::GetFileName($path)) by $($sig.SignerCertificate.Subject)"
}
if ($Sign) { SignFile $exe }

# --- 4. package -------------------------------------------------------------------------------------------------
'4/5 package'
Get-ChildItem $dist -File | Remove-Item -Force
$portable = Join-Path $dist "WarpSwitch-$Version-portable.exe"
Copy-Item $exe $portable

if (-not $SkipInstaller) {
  $iscc = @($env:ISCC, (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it (winget install JRSoftware.InnoSetup) or pass -SkipInstaller.' }
  & $iscc /Q "/DAppVersion=$Version" "/DSourceExe=$exe" "/O$dist" (Join-Path $root 'installer\WarpSwitch.iss')
  if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
  if ($Sign) { SignFile (Join-Path $dist "WarpSwitch-Setup-$Version.exe") }
}

# --- 5. checksums (the in-app updater only runs an installer whose hash is listed here) ---------------------
'5/5 checksums'
$sums = Get-ChildItem $dist -File -Filter *.exe | Sort-Object Name | ForEach-Object { (Sha256 $_.FullName).ToLowerInvariant() + '  ' + $_.Name }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), ($sums -join "`n") + "`n")
Get-ChildItem $dist -File | ForEach-Object { "  {0,-36} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB) }
