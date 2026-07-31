[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $YakPath,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionPropsPath = Join-Path $repository 'Directory.Build.props'
[xml] $versionProps = Get-Content -Raw -LiteralPath $versionPropsPath
$versionNodes = @($versionProps.SelectNodes('//VersionPrefix'))
if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versionNodes[0].InnerText)) {
    throw 'Directory.Build.props must contain exactly one non-empty VersionPrefix.'
}
$projectVersion = $versionNodes[0].InnerText.Trim()
if ($Version -cne $projectVersion) {
    throw "Package version '$Version' does not match VersionPrefix '$projectVersion'."
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
}

$source = Join-Path $repository 'src\InstantMeshPlus.Grasshopper\bin\Release\net8.0-windows'
$packageRoot = Join-Path $repository 'artifacts\yak\package'
$artifactRoot = Join-Path $repository 'artifacts\yak'
$resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)
$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
if (-not $resolvedPackageRoot.StartsWith($resolvedArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to prepare a package outside artifacts\yak.'
}
New-Item -ItemType Directory -Path $resolvedArtifactRoot -Force | Out-Null
if (Test-Path -LiteralPath $resolvedPackageRoot) {
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}
Get-ChildItem -LiteralPath $resolvedArtifactRoot -File -Filter '*.yak' |
    Remove-Item -Force
New-Item -ItemType Directory -Path $resolvedPackageRoot -Force | Out-Null

@(
    'InstantMeshPlus.Grasshopper.gha',
    'InstantMeshPlus.Core.dll',
    'InstantMeshPlus.Worker.exe'
) | ForEach-Object {
    $item = Join-Path $source $_
    if (-not (Test-Path -LiteralPath $item)) {
        throw "Missing release package file: $item"
    }
    Copy-Item -LiteralPath $item -Destination $resolvedPackageRoot
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $resolvedPackageRoot
Copy-Item -LiteralPath (Join-Path $repository 'THIRD_PARTY_NOTICES.md') -Destination $resolvedPackageRoot
Copy-Item -LiteralPath (Join-Path $repository 'third_party\instant-meshes\LICENSE.txt') -Destination (Join-Path $resolvedPackageRoot 'INSTANT_MESHES_LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repository 'third_party\GrasshopperAsyncComponent\LICENSE') -Destination (Join-Path $resolvedPackageRoot 'GRASSHOPPER_ASYNC_COMPONENT_LICENSE.txt')

$manifestTemplate = Get-Content -Raw (Join-Path $repository 'yak\manifest.yml')
$manifest = $manifestTemplate.Replace('__VERSION__', $Version)
$manifestPath = Join-Path $resolvedPackageRoot 'manifest.yml'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

$resolvedYakPath = $null
if (-not [string]::IsNullOrWhiteSpace($YakPath)) {
    if (-not (Test-Path -LiteralPath $YakPath -PathType Leaf)) {
        throw "Yak CLI was not found at '$YakPath'."
    }
    $resolvedYakPath = (Resolve-Path -LiteralPath $YakPath).Path
} else {
    $yak = Get-Command yak.exe -ErrorAction SilentlyContinue
    if ($null -ne $yak) {
        $resolvedYakPath = $yak.Source
    } else {
        $rhinoYak = 'C:\Program Files\Rhino 8\System\yak.exe'
        if (Test-Path -LiteralPath $rhinoYak -PathType Leaf) {
            $resolvedYakPath = $rhinoYak
        }
    }
}
if ([string]::IsNullOrWhiteSpace($resolvedYakPath)) {
    throw 'Yak CLI was not found. Pass -YakPath or install Rhino 8.'
}

Push-Location $resolvedPackageRoot
try {
    & $resolvedYakPath build --platform win
    if ($LASTEXITCODE -ne 0) { throw 'Yak package build failed.' }
    $archives = @(Get-ChildItem -LiteralPath $resolvedPackageRoot -File -Filter '*.yak')
    if ($archives.Count -ne 1) {
        throw "Expected exactly one Yak archive, found $($archives.Count)."
    }
    $archives | Move-Item -Destination $resolvedArtifactRoot -Force
} finally {
    Pop-Location
}

$finalArchives = @(Get-ChildItem -LiteralPath $resolvedArtifactRoot -File -Filter '*.yak')
if ($finalArchives.Count -ne 1) {
    throw "Expected exactly one final Yak archive, found $($finalArchives.Count)."
}
Write-Output $finalArchives[0].FullName
