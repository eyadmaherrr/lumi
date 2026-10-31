# Copies the latest installer and screenshots into site\ and deploys the website to Vercel.
#   powershell -ExecutionPolicy Bypass -File publish-site.ps1            production deploy
#   powershell -ExecutionPolicy Bypass -File publish-site.ps1 -Preview   preview deploy only
#   powershell -ExecutionPolicy Bypass -File publish-site.ps1 -NoDeploy  just copy the files
# Run build.ps1 first so dist\LumiSetup.exe is current.
param([switch]$Preview, [switch]$NoDeploy)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$site = "$here\site"

if (-not (Test-Path "$here\dist\LumiSetup.exe")) { throw "dist\LumiSetup.exe not found. Run build.ps1 first." }
New-Item -ItemType Directory -Force "$site\download", "$site\images" | Out-Null
Copy-Item "$here\dist\LumiSetup.exe" "$site\download\LumiSetup.exe" -Force
Copy-Item "$here\docs\images\*.png" "$site\images\" -Force
Write-Host "Copied LumiSetup.exe and screenshots into site\"

if ($NoDeploy) { return }
Push-Location $site
try {
    if ($Preview) { vercel deploy --yes } else { vercel deploy --prod --yes }
    if ($LASTEXITCODE -ne 0) { throw "Vercel deploy failed" }
}
finally { Pop-Location }
