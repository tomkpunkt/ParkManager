$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$uiRoot = Join-Path $repoRoot 'UI'
$project = Join-Path $repoRoot 'tests\PlazaBatch\PlazaBatch.csproj'
$serverDll = Join-Path $repoRoot 'tests\PlazaBatch\bin\Debug\net8.0\PlazaBatch.dll'
$server = $null
$exitCode = 1

try {
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'The geometry test host did not build.' }
    Push-Location $uiRoot
    try {
        & npm run mock:build
        if ($LASTEXITCODE -ne 0) { throw 'The mock UI did not build.' }

        $ready = $false
        try {
            $ready = (Invoke-WebRequest -Uri 'http://localhost:8765/' -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200
        } catch { }
        if (-not $ready) {
            $server = Start-Process -FilePath 'dotnet' -ArgumentList @($serverDll, '--serve', '8765') `
                -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
            for ($attempt = 0; $attempt -lt 50 -and -not $ready; $attempt++) {
                Start-Sleep -Milliseconds 200
                try {
                    $ready = (Invoke-WebRequest -Uri 'http://localhost:8765/' -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200
                } catch { }
            }
            if (-not $ready) { throw 'The mock server did not start on port 8765.' }
        }
        & npx playwright test
        $exitCode = $LASTEXITCODE
    } finally { Pop-Location }
} catch {
    Write-Error $_
} finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}
exit $exitCode
