# Stop, build, start.
#
#   powershell -File tools/restart.ps1
#
# Windows holds the exe open while the app runs, so a build during it fails
# with MSB3021 and leaves the old binary serving - which looks exactly like a
# change that did not work. Stopping first makes that impossible.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'AutoPartsHub.Api'
$log = Join-Path $root 'api.log'

Get-Process -Name AutoPartsHub.Api -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Push-Location $root
try {
    $build = & dotnet build 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | Select-String -Pattern 'error' | Select-Object -First 10
        throw "build failed"
    }
    "build ok"
} finally {
    Pop-Location
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5080'

Start-Process -FilePath 'dotnet' `
    -ArgumentList 'run', '--no-build', '--no-launch-profile', '--project', $project `
    -WorkingDirectory $project `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err" `
    -WindowStyle Hidden

# Wait for it rather than assuming: returning before the port is open makes
# the next command's failure look like the code's fault.
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $r = Invoke-WebRequest -Uri 'http://localhost:5080/health' -TimeoutSec 3 -UseBasicParsing
        if ($r.StatusCode -eq 200) { "listening on :5080"; exit 0 }
    } catch { }
}

"did not come up. Last lines of $log :"
Get-Content $log -Tail 15 -ErrorAction SilentlyContinue
exit 1
