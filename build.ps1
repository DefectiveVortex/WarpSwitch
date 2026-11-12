# build.ps1 - builds "WARP Switch.exe" (single file) and copies it to the Desktop.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$res  = Join-Path $root 'res'
$bin  = Join-Path $root '.deps'   # where the verified usque release zip + signed wintun.dll live
if (-not (Test-Path $res)) { New-Item -ItemType Directory -Path $res | Out-Null }

function Sha256($path) {
  $s = [IO.File]::OpenRead($path)
  try { -join ([Security.Cryptography.SHA256]::Create().ComputeHash($s) | ForEach-Object { $_.ToString('X2') }) } finally { $s.Dispose() }
}

# --- 1. verified inputs -------------------------------------------------------------------
$usqueZipSha = 'F6F7F0A1A2BC9BCC15CF563EC1F892D00690A92C086B23ED3211B802209099E7'  # usque_4.2.1_windows_amd64.zip (official checksums.txt)
$wintunSha   = 'E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE'  # wintun 0.14.1 amd64, WireGuard-signed
if ((Sha256 "$bin\usque.zip") -ne $usqueZipSha) { throw 'usque.zip checksum mismatch' }
if ((Sha256 "$bin\wintun.dll") -ne $wintunSha)  { throw 'wintun.dll checksum mismatch' }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function GzipTo([IO.Stream]$in, [string]$out) {
  $o = [IO.File]::Create($out)
  $gz = New-Object IO.Compression.GZipStream($o, [IO.Compression.CompressionLevel]::Optimal)
  try { $in.CopyTo($gz) } finally { $gz.Dispose(); $o.Dispose() }
}
$zip = [IO.Compression.ZipFile]::OpenRead("$bin\usque.zip")
try {
  $entry = $zip.Entries | Where-Object { $_.Name -eq 'usque.exe' } | Select-Object -First 1
  $s = $entry.Open(); try { GzipTo $s "$res\usque.exe.gz" } finally { $s.Dispose() }
} finally { $zip.Dispose() }
$s = [IO.File]::OpenRead("$bin\wintun.dll"); try { GzipTo $s "$res\wintun.dll.gz" } finally { $s.Dispose() }

# --- 2. app icon (the Vortex mark: sunset-gradient disc with a white three-arm swirl), PNG-compressed .ico
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
$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i]; $len = $frames[$i].Length
  $bw.Write([byte]($n % 256)); $bw.Write([byte]($n % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
  $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
[IO.File]::WriteAllBytes("$res\app.ico", $ico.ToArray())

# --- 3. compile ---------------------------------------------------------------------------
Push-Location $root
try {
  & dotnet build -c Release -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

$exe = Join-Path $root 'bin\Release\net48\WarpSwitch.exe'
$out = Join-Path $root 'WARP Switch.exe'

# --- 4. sign it as Vortex (self-made code-signing certificate, kept in this user's certificate store) -----
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=Vortex' -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Vortex' -FriendlyName 'Vortex code signing' -CertStoreLocation Cert:\CurrentUser\My `
          -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
}
$sig = $null
try { $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction Stop } catch { }
if (-not $sig -or -not $sig.SignerCertificate) { $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 }
"signed by: $($sig.SignerCertificate.Subject)  timestamp: $([bool]$sig.TimeStamperCertificate)"
if (Test-Path $out) {
  # a running copy keeps the file locked, but Windows lets us rename it out of the way
  Remove-Item "$out.old" -Force -ErrorAction SilentlyContinue
  try { Remove-Item $out -Force -ErrorAction Stop } catch { Rename-Item $out "$out.old" }
}
Copy-Item $exe $out -Force
"built: $out  ($([Math]::Round((Get-Item $out).Length / 1MB, 2)) MB)"
