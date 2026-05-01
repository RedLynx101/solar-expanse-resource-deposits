param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Solar Expanse",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\SolarExpanse.ResourceDeposits\SolarExpanse.ResourceDeposits.csproj"
$pluginOut = Join-Path $PSScriptRoot "..\src\SolarExpanse.ResourceDeposits\bin\Release\netstandard2.1\SolarExpanse.ResourceDeposits.dll"
$deployDir = Join-Path $GameDir "BepInEx\plugins\SolarExpanse.ResourceDeposits"
$configDir = Join-Path $GameDir "BepInEx\config"
$configSource = Join-Path $PSScriptRoot "..\config\SolarExpanse.ResourceDeposits.json"

dotnet build $project -c Release -p:GameDir="$GameDir"

if ($Deploy) {
    if (-not (Test-Path $pluginOut)) {
        throw "Build output missing: $pluginOut"
    }

    New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null

    Copy-Item -LiteralPath $pluginOut -Destination (Join-Path $deployDir "SolarExpanse.ResourceDeposits.dll") -Force
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $configDir "SolarExpanse.ResourceDeposits.json") -Force

    Write-Host "Deployed plugin to $deployDir"
    Write-Host "Deployed config to $configDir"
}
