param([ValidateRange(1, 65535)][int]$Port = 8765)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$uiRoot = Join-Path $repoRoot 'UI'
$project = Join-Path $repoRoot 'tests\PlazaBatch\PlazaBatch.csproj'

& dotnet build $project
if ($LASTEXITCODE -ne 0) { throw 'The geometry test host did not build.' }

Push-Location $uiRoot
try {
    & npm run mock:build
    if ($LASTEXITCODE -ne 0) { throw 'The mock UI did not build.' }
} finally { Pop-Location }

Write-Host "Open http://localhost:$Port/ (Ctrl+C to stop)."
Push-Location $repoRoot
try {
    & dotnet run --no-build --project $project -- --serve $Port
    if ($LASTEXITCODE -ne 0) { throw 'The mock server failed.' }
} finally { Pop-Location }
