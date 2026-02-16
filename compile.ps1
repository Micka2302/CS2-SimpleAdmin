#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$mainProject = Join-Path $root 'CS2-SimpleAdmin/CS2-SimpleAdmin.csproj'
$apiProject = Join-Path $root 'CS2-SimpleAdminApi/CS2-SimpleAdminApi.csproj'
$versionFile = Join-Path $root 'CS2-SimpleAdmin/VERSION'
$kitsuneSharedSource = Join-Path $root 'KitsuneMenu/KitsuneMenu.dll'
$kitsuneProjectReferencePath = Join-Path $root 'CS2-SimpleAdmin/3rd_party/KitsuneMenu.dll'

$compiledRoot = Join-Path $root 'compiled'
$tmpRoot = Join-Path $root '.build-tmp'
$cssRoot = Join-Path $compiledRoot 'counterstrikesharp'
$pluginsTargetRoot = Join-Path $cssRoot 'plugins'
$sharedTargetRoot = Join-Path $cssRoot 'shared'

if (-not (Test-Path $mainProject)) {
    throw "Main project not found: $mainProject"
}
if (-not (Test-Path $apiProject)) {
    throw "API project not found: $apiProject"
}
if (-not (Test-Path $kitsuneSharedSource)) {
    throw "KitsuneMenu shared dependency not found: $kitsuneSharedSource"
}

$moduleProjects = Get-ChildItem (Join-Path $root 'Modules') -Directory |
    ForEach-Object {
        Get-ChildItem $_.FullName -Filter *.csproj -File -ErrorAction SilentlyContinue
    } |
    Sort-Object FullName

$pluginOutputAllowList = @{
    'CS2-SimpleAdmin_BanCheckModule' = @{
        Files = @(
            'CounterStrikeSharp.API.dll'
            'CS2-SimpleAdmin_BanCheckModule.deps.json'
            'CS2-SimpleAdmin_BanCheckModule.dll'
            'CS2-SimpleAdmin_BanCheckModule.pdb'
            'CS2-SimpleAdminApi.dll'
            'Dapper.dll'
            'MySqlConnector.dll'
            'System.Data.SQLite.dll'
        )
        Directories = @(
            'lang'
        )
    }
}

function Build-Project {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $projectName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    Write-Host "[BUILD] $projectName"

    & dotnet restore $ProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for $projectName (exit code: $LASTEXITCODE)"
    }

    & dotnet build $ProjectPath -c $Configuration --no-restore --nologo -o $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $projectName (exit code: $LASTEXITCODE)"
    }

    if (-not (Test-Path $OutputPath)) {
        throw "Build output not found for $projectName at $OutputPath"
    }

    $expectedDll = Join-Path $OutputPath "$projectName.dll"
    if (-not (Test-Path $expectedDll)) {
        throw "Expected output DLL not found for $projectName at $expectedDll"
    }
}

function Apply-PluginOutputFilter {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PluginName,
        [Parameter(Mandatory = $true)]
        [string]$PluginPath
    )

    if (-not $pluginOutputAllowList.ContainsKey($PluginName)) {
        return
    }

    $filter = $pluginOutputAllowList[$PluginName]
    $allowedFiles = @($filter.Files)
    $allowedDirectories = @($filter.Directories)

    Write-Host "[FILTER] $PluginName"

    Get-ChildItem -Path $PluginPath -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            if ($allowedDirectories -notcontains $_.Name) {
                Remove-Item -Path $_.FullName -Recurse -Force
            }
            return
        }

        if ($allowedFiles -notcontains $_.Name) {
            Remove-Item -Path $_.FullName -Force
        }
    }
}

# Clean previous artifacts
Remove-Item -Recurse -Force $tmpRoot -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null

try {
    # Keep project reference DLL in sync with the provided KitsuneMenu artifact.
    Copy-Item -Path $kitsuneSharedSource -Destination $kitsuneProjectReferencePath -Force

    # Build core plugin + shared API
    $mainOutput = Join-Path $tmpRoot 'plugins/CS2-SimpleAdmin'
    $apiOutput = Join-Path $tmpRoot 'shared/CS2-SimpleAdminApi'

    Build-Project -ProjectPath $mainProject -OutputPath $mainOutput
    Build-Project -ProjectPath $apiProject -OutputPath $apiOutput

    # Build modules
    foreach ($moduleProject in $moduleProjects) {
        $moduleName = [IO.Path]::GetFileNameWithoutExtension($moduleProject.Name)
        $moduleOutput = Join-Path $tmpRoot "plugins/$moduleName"
        Build-Project -ProjectPath $moduleProject.FullName -OutputPath $moduleOutput
    }

    # Replace final output only after successful builds
    Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $pluginsTargetRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $sharedTargetRoot -Force | Out-Null

    # Stage plugins
    Get-ChildItem (Join-Path $tmpRoot 'plugins') -Directory | ForEach-Object {
        $target = Join-Path $pluginsTargetRoot $_.Name
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $_.FullName '*') -Destination $target -Recurse -Force
        Apply-PluginOutputFilter -PluginName $_.Name -PluginPath $target
    }

    # Stage shared API
    $sharedApiTarget = Join-Path $sharedTargetRoot 'CS2-SimpleAdminApi'
    New-Item -ItemType Directory -Path $sharedApiTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $apiOutput '*') -Destination $sharedApiTarget -Recurse -Force

    # Stage shared KitsuneMenu to keep server shared dependency in sync
    $kitsuneSharedTarget = Join-Path $sharedTargetRoot 'KitsuneMenu'
    New-Item -ItemType Directory -Path $kitsuneSharedTarget -Force | Out-Null
    Copy-Item -Path $kitsuneSharedSource -Destination (Join-Path $kitsuneSharedTarget 'KitsuneMenu.dll') -Force

    # Create zip package
    $version = if (Test-Path $versionFile) {
        (Get-Content $versionFile -Raw).Trim()
    } else {
        Get-Date -Format 'yyyyMMddHHmmss'
    }

    $zipPath = Join-Path $compiledRoot "CS2-SimpleAdmin-$version.zip"
    if (-not $NoZip) {
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $cssRoot '*') -DestinationPath $zipPath
    }

    Write-Host "[OK] Build finished."
    Write-Host " - Output: $cssRoot"
    if (-not $NoZip) {
        Write-Host " - Zip:    $zipPath"
    }
}
finally {
    Remove-Item -Recurse -Force $tmpRoot -ErrorAction SilentlyContinue
}
