[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.0',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
if (Test-Path -LiteralPath $resolvedPackageRoot) {
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}
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

$manifestTemplate = Get-Content -Raw (Join-Path $repository 'yak\manifest.yml')
$manifest = $manifestTemplate.Replace('__VERSION__', $Version)
$manifestPath = Join-Path $resolvedPackageRoot 'manifest.yml'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

$yak = Get-Command yak.exe -ErrorAction SilentlyContinue
if ($null -eq $yak) {
    Write-Warning "yak.exe was not found. Package payload is ready at $resolvedPackageRoot"
    return
}
Push-Location $resolvedPackageRoot
try {
    & $yak.Source build --platform win
    if ($LASTEXITCODE -ne 0) { throw 'Yak package build failed.' }
    Get-ChildItem -LiteralPath $resolvedPackageRoot -Filter '*.yak' |
        Move-Item -Destination $resolvedArtifactRoot -Force
} finally {
    Pop-Location
}
