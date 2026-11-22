$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ClaimCRAFTs\ClaimCRAFTs.csproj'
& dotnet run --project $project
if ($LASTEXITCODE -ne 0) { throw "Application exited with status $LASTEXITCODE" }
