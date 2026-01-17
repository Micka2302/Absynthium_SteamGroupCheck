#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcRoot = Join-Path $root 'src'
Set-Location $root

$compiledRoot = Join-Path $root 'compiled'
Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $compiledRoot | Out-Null

$pluginTarget = Join-Path $compiledRoot 'counterstrikesharp/plugins/Absynthium_SteamGroupCheck'
$configTarget = Join-Path $compiledRoot 'counterstrikesharp/configs/plugins/Absynthium_SteamGroupCheck'

Remove-Item -Recurse -Force $pluginTarget -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $configTarget -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
New-Item -ItemType Directory -Path $configTarget -Force | Out-Null

dotnet restore (Join-Path $srcRoot 'Absynthium_SteamGroupCheck.sln')

Write-Host '[INFO] Build Absynthium_SteamGroupCheck...'
dotnet build (Join-Path $srcRoot 'Absynthium_SteamGroupCheck.csproj') -c Release -f net8.0 --nologo -p:OutputPath=$pluginTarget -p:AppendTargetFrameworkToOutputPath=false -p:AppendRuntimeIdentifierToOutputPath=false

$langSource = Join-Path $srcRoot 'lang'
$langTarget = Join-Path $pluginTarget 'lang'
if (Test-Path $langSource) {
  New-Item -ItemType Directory -Path $langTarget -Force | Out-Null
  Copy-Item -Path (Join-Path $langSource '*') -Destination $langTarget -Recurse -Force
  Write-Host "  -> Langues copiees dans $langTarget"
} else {
  Write-Warning "Dossier de langues introuvable: $langSource"
}

$configSource = Join-Path $srcRoot 'configs/plugins/Absynthium_SteamGroupCheck'
if (Test-Path $configSource) {
  Copy-Item -Path (Join-Path $configSource '*') -Destination $configTarget -Recurse -Force
  Write-Host "  -> Configuration copiee dans $configTarget"
} else {
  Write-Warning "Dossier de configuration introuvable: $configSource"
}

if (-not (Test-Path (Join-Path $pluginTarget 'Absynthium_SteamGroupCheck.dll'))) {
  throw "DLL non trouvee dans $pluginTarget (build rate ?)"
}

Write-Host "  -> DLL generees directement dans $pluginTarget"
Write-Host "[OK] Artifacts prets dans $compiledRoot"
