$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ClaimCRAFTs\ClaimCRAFTs.csproj'
$output = Join-Path $PSScriptRoot 'publish\win-x64'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required. Install from https://dotnet.microsoft.com/download/dotnet/10.0.'
}
$versionText = (& dotnet --version).Trim()
if ([int]($versionText.Split('.')[0]) -lt 10) { throw ".NET 10 SDK or later required, found $versionText." }
Write-Host "Building ClaimCRAFTs v0.3.3 for Windows x64 ($versionText)..." -ForegroundColor Cyan
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $output `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
$exe = Join-Path $output 'ClaimCRAFTs.exe'
if (-not (Test-Path $exe)) { throw 'Build finished without a ClaimCRAFTs.exe.' }
Write-Host "Build complete: $exe" -ForegroundColor Green
