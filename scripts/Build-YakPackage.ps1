[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [ValidateSet('win', 'mac')]
    [string] $Platform = 'win',
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
    if ($Platform -eq 'win') {
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    } else {
        if (-not $IsMacOS) {
            throw 'The mac platform package must be built on macOS.'
        }
        & bash (Join-Path $PSScriptRoot 'build-macos.sh') Release
        if ($LASTEXITCODE -ne 0) { throw 'macOS build failed.' }
    }
}

$source = Join-Path $repository 'src/InstantMeshPlus.Grasshopper/bin/Release/net8.0'
$yakRoot = Join-Path $repository 'artifacts/yak'
$packageRoot = Join-Path $yakRoot "package-$Platform"
$artifactRoot = Join-Path $yakRoot $Platform
$resolvedYakRoot = [System.IO.Path]::GetFullPath($yakRoot)
$resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)
$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
foreach ($candidate in @($resolvedPackageRoot, $resolvedArtifactRoot)) {
    if (-not $candidate.StartsWith($resolvedYakRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to prepare a package outside artifacts/yak.'
    }
}

New-Item -ItemType Directory -Path $resolvedArtifactRoot -Force | Out-Null
if (Test-Path -LiteralPath $resolvedPackageRoot) {
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}
Get-ChildItem -LiteralPath $resolvedArtifactRoot -File -Filter '*.yak' |
    Remove-Item -Force
New-Item -ItemType Directory -Path $resolvedPackageRoot -Force | Out-Null

$runtimeFiles = @(
    'InstantMeshPlus.Grasshopper.gha',
    'InstantMeshPlus.Core.dll'
)
if ($Platform -eq 'win') {
    $runtimeFiles += @('InstantMeshPlus.Worker.exe', 'tbb12.dll')
} else {
    $runtimeFiles += @('InstantMeshPlus.Worker', 'libtbb.12.dylib')
}
foreach ($fileName in $runtimeFiles) {
    $item = Join-Path $source $fileName
    if (-not (Test-Path -LiteralPath $item -PathType Leaf)) {
        throw "Missing release package file: $item"
    }
    Copy-Item -LiteralPath $item -Destination $resolvedPackageRoot
}

if ($Platform -eq 'mac') {
    foreach ($fileName in @('InstantMeshPlus.Worker', 'libtbb.12.dylib')) {
        & chmod 755 (Join-Path $resolvedPackageRoot $fileName)
        if ($LASTEXITCODE -ne 0) { throw "Could not set executable permissions on $fileName." }
    }
}

Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $resolvedPackageRoot
Copy-Item -LiteralPath (Join-Path $repository 'THIRD_PARTY_NOTICES.md') -Destination $resolvedPackageRoot
$thirdPartyFiles = @(
    @{ Source = 'third_party/instant-meshes/LICENSE.txt'; Destination = 'INSTANT_MESHES_LICENSE.txt' }
    @{ Source = 'third_party/json/LICENSE.MIT'; Destination = 'NLOHMANN_JSON_LICENSE.txt' }
    @{ Source = 'third_party/oneTBB/LICENSE.txt'; Destination = 'TBB_LICENSE.txt' }
    @{ Source = 'third_party/GrasshopperAsyncComponent/LICENSE'; Destination = 'APACHE-2.0_LICENSE.txt' }
    @{ Source = 'licenses/PCG32_NOTICE.txt'; Destination = 'PCG32_NOTICE.txt' }
    @{ Source = 'third_party/instant-meshes/ext/pss/LICENSE.txt'; Destination = 'PSS_LICENSE.txt' }
    @{ Source = 'third_party/instant-meshes/ext/dset/LICENSE.txt'; Destination = 'DSET_LICENSE.txt' }
    @{ Source = 'third_party/instant-meshes/ext/nanogui/ext/eigen/COPYING.MPL2'; Destination = 'EIGEN_COPYING_MPL2.txt' }
    @{ Source = 'third_party/instant-meshes/ext/nanogui/ext/eigen/COPYING.README'; Destination = 'EIGEN_COPYING_README.txt' }
    @{ Source = 'third_party/instant-meshes/ext/nanogui/ext/eigen/COPYING.BSD'; Destination = 'EIGEN_COPYING_BSD.txt' }
    @{ Source = 'third_party/instant-meshes/ext/nanogui/ext/eigen/COPYING.MINPACK'; Destination = 'EIGEN_COPYING_MINPACK.txt' }
    @{ Source = 'third_party/GrasshopperAsyncComponent/LICENSE'; Destination = 'GRASSHOPPER_ASYNC_COMPONENT_LICENSE.txt' }
)
foreach ($entry in $thirdPartyFiles) {
    $licenseSource = Join-Path $repository $entry.Source
    if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
        throw "Missing third-party license file: $licenseSource"
    }
    Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $resolvedPackageRoot $entry.Destination)
}

$packageIcon = Join-Path $repository 'yak/icon.png'
if (-not (Test-Path -LiteralPath $packageIcon -PathType Leaf)) {
    throw "Missing Yak package icon: $packageIcon"
}
Copy-Item -LiteralPath $packageIcon -Destination (Join-Path $resolvedPackageRoot 'icon.png')

$manifestTemplate = Get-Content -Raw (Join-Path $repository 'yak/manifest.yml')
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
} elseif ($Platform -eq 'win') {
    $yak = Get-Command yak.exe -ErrorAction SilentlyContinue
    if ($null -ne $yak) {
        $resolvedYakPath = $yak.Source
    } else {
        $rhinoYak = 'C:\Program Files\Rhino 8\System\yak.exe'
        if (Test-Path -LiteralPath $rhinoYak -PathType Leaf) {
            $resolvedYakPath = $rhinoYak
        }
    }
} else {
    $rhinoYak = '/Applications/Rhino 8.app/Contents/Resources/bin/yak'
    if (Test-Path -LiteralPath $rhinoYak -PathType Leaf) {
        $resolvedYakPath = $rhinoYak
    }
}
if ([string]::IsNullOrWhiteSpace($resolvedYakPath)) {
    throw 'Yak CLI was not found. Pass -YakPath or install Rhino 8.'
}

Push-Location $resolvedPackageRoot
try {
    & $resolvedYakPath build --platform $Platform
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
    throw "Expected exactly one final $Platform Yak archive, found $($finalArchives.Count)."
}
Write-Output $finalArchives[0].FullName
