$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$uiRoot = Join-Path $repoRoot 'UI'
$project = Join-Path $repoRoot 'tests\PlazaBatch\PlazaBatch.csproj'
$serverDll = Join-Path $repoRoot 'tests\PlazaBatch\bin\Debug\net8.0\PlazaBatch.dll'
$server = $null
$exitCode = 1
$previousPort = $env:PARKMANAGER_MOCK_PORT

try {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $env:PARKMANAGER_MOCK_PORT = [string]$port

    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'The geometry test host did not build.' }
    Push-Location $uiRoot
    try {
        & npm run mock:build
        if ($LASTEXITCODE -ne 0) { throw 'The mock UI did not build.' }

        $server = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$serverDll`"", '--serve', $port) `
            -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
        $ready = $false
        for ($attempt = 0; $attempt -lt 50 -and -not $ready; $attempt++) {
            Start-Sleep -Milliseconds 200
            if ($server.HasExited) { throw 'The mock server exited before becoming ready.' }
            try {
                $ready = (Invoke-WebRequest -Uri "http://localhost:$port/" -UseBasicParsing -TimeoutSec 1).StatusCode -eq 200
            } catch { }
        }
        if (-not $ready) { throw "The mock server did not start on port $port." }
        & npx playwright test
        $exitCode = $LASTEXITCODE
    } finally { Pop-Location }
} catch {
    Write-Error $_
} finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
    $env:PARKMANAGER_MOCK_PORT = $previousPort
}
exit $exitCode
