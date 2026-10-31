# Builds Lumi.exe and its installer LumiSetup.exe (into dist\) with the C# compiler that ships
# with Windows (.NET Framework 4). No SDK needed. Run:  powershell -ExecutionPolicy Bypass -File build.ps1
#
# Signing: Smart App Control only runs unknown programs that are signed with a certificate
# from a trusted authority. Both exes are signed with signtool when one of these is set up:
#   signing.json next to this script     Azure Trusted Signing / Artifact Signing (copy signing.example.json).
#                                        Sign in first with `az login` (or any Azure credential).
#   -Pfx <file.pfx> [-PfxPassword <pw>]  a certificate file
#   -Thumbprint <hex>                    a certificate in your Windows certificate store
# signtool comes from: winget install Microsoft.Azure.ArtifactSigningClientTools
# Self-signed certificates won't satisfy Smart App Control.
param([string]$Pfx, [string]$PfxPassword, [string]$Thumbprint)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$winmd = "$env:WINDIR\System32\WinMetadata"   # WinRT APIs: media, notifications, Bluetooth
$dist = "$here\dist"
New-Item -ItemType Directory -Force $dist | Out-Null

Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# App icon: dark rounded square holding a black "island" pill with a green dot and equalizer bars.
function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $bg = New-RoundRect 0 0 $s $s ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $s), ([System.Drawing.Color]::FromArgb(44, 44, 48)), ([System.Drawing.Color]::FromArgb(10, 10, 12))
    $g.FillPath($grad, $bg)

    $pw = $s * 0.76; $ph = $s * 0.32; $px = ($s - $pw) / 2; $py = ($s - $ph) / 2
    $pill = New-RoundRect $px $py $pw $ph ($ph / 2)
    $g.FillPath([System.Drawing.Brushes]::Black, $pill)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 70, 74)), ([Math]::Max(1, $s / 64))
    $g.DrawPath($pen, $pill)

    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(48, 209, 88))
    $dd = $ph * 0.42
    $g.FillEllipse($green, $px + $ph / 2 - $dd / 2, $s / 2 - $dd / 2, $dd, $dd)
    $bw = [Math]::Max(1, $s * 0.045); $gap = $s * 0.075
    $heights = 0.38, 0.62, 0.45
    for ($i = 0; $i -lt 3; $i++) {
        $bh = $ph * $heights[$i]
        $bx = $px + $pw - $ph / 2 - (2 - $i) * $gap - $bw / 2
        $g.FillRectangle($green, $bx, $s / 2 - $bh / 2, $bw, $bh)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Write a PNG-compressed .ico (supported since Windows Vista).
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @($sizes | ForEach-Object { , (New-IconPng $_) })
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$dist\Lumi.ico", $ico.ToArray())

# ---- Signing (optional) ----
# Newest x64 file with this name in the given folders (signtool and the Azure signing plug-in).
function Find-Tool([string]$name, [string[]]$roots) {
    $roots | Where-Object { $_ -and (Test-Path $_) } |
        ForEach-Object { Get-ChildItem $_ -Recurse -Filter $name -ErrorAction SilentlyContinue } |
        Sort-Object @{ e = { $_.FullName -match '\x64\' } }, @{ e = { $_.VersionInfo.FileVersion } } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
$toolRoots = "$env:ProgramFiles\Microsoft\ArtifactSigningClientTools", "${env:ProgramFiles(x86)}\Microsoft\ArtifactSigningClientTools",
    "$env:ProgramFiles\Microsoft\TrustedSigningClientTools", "${env:ProgramFiles(x86)}\Microsoft\TrustedSigningClientTools",
    "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools", "$env:LOCALAPPDATA\Microsoft\MicrosoftTrustedSigningClientTools",
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin"
$metadata = "$here\signing.json"

# Timestamped, so the signature stays valid after the certificate itself expires.
$signArgs = $null
if (Test-Path $metadata) {
    $dlib = Find-Tool "Azure.CodeSigning.Dlib.dll" $toolRoots
    if (-not $dlib) { throw "Azure signing plug-in not found. Run: winget install Microsoft.Azure.ArtifactSigningClientTools" }
    $signArgs = @("/tr", "http://timestamp.acs.microsoft.com", "/td", "SHA256", "/dlib", $dlib, "/dmdf", $metadata)
} elseif ($Pfx) {
    $signArgs = @("/tr", "http://timestamp.digicert.com", "/td", "SHA256", "/f", (Resolve-Path $Pfx).Path)
    if ($PfxPassword) { $signArgs += @("/p", $PfxPassword) }
} elseif ($Thumbprint) {
    $signArgs = @("/tr", "http://timestamp.digicert.com", "/td", "SHA256", "/sha1", $Thumbprint)
}

function Sign-File([string]$file) {
    if (-not $signArgs) {
        Write-Warning "Not signed: $file (add signing.json or pass -Pfx / -Thumbprint). Smart App Control may block it."
        return
    }
    $signtool = Find-Tool "signtool.exe" $toolRoots
    if (-not $signtool) { throw "signtool.exe not found. Run: winget install Microsoft.Azure.ArtifactSigningClientTools" }
    & $signtool sign /v /fd SHA256 @signArgs $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed (signtool exit code $LASTEXITCODE)" }
    & $signtool verify /pa $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Signed, but Windows doesn't trust the signature" }
    Write-Host "Signed $file"
}

# ---- Lumi.exe ----
& "$fw\csc.exe" /nologo /target:winexe /optimize+ `
    /out:"$dist\Lumi.exe" `
    /win32icon:"$dist\Lumi.ico" `
    /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" `
    /r:"$fw\System.Xaml.dll" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Core.dll /r:"$fw\System.Runtime.dll" /r:"$fw\System.Runtime.WindowsRuntime.dll" /r:"$fw\System.Management.dll" /r:"$fw\System.Web.Extensions.dll" `
    /r:"$winmd\Windows.Foundation.winmd" /r:"$winmd\Windows.Media.winmd" /r:"$winmd\Windows.Storage.winmd" `
    /r:"$winmd\Windows.UI.winmd" /r:"$winmd\Windows.ApplicationModel.winmd" /r:"$winmd\Windows.Devices.winmd" `
    "$here\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed: Lumi.exe" }
Write-Host "Built $dist\Lumi.exe"
# Signed before it's packed into the installer, so the installed copy is signed too.
Sign-File "$dist\Lumi.exe"

# ---- LumiSetup.exe (carries Lumi.exe inside) ----
& "$fw\csc.exe" /nologo /target:winexe /optimize+ `
    /out:"$dist\LumiSetup.exe" `
    /win32icon:"$dist\Lumi.ico" `
    /resource:"$dist\Lumi.exe",Lumi.exe `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$here\installer\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed: LumiSetup.exe" }
Write-Host "Built $dist\LumiSetup.exe"
Sign-File "$dist\LumiSetup.exe"
