param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$runtime = "win-$Architecture"
$project = Join-Path $PSScriptRoot 'ApexChime.csproj'
$output = Join-Path $PSScriptRoot "dist/$runtime"

dotnet publish $project -c Release -r $runtime --self-contained -o $output `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-Item (Join-Path $output 'ApexChime.exe')
