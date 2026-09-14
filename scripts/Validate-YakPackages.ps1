[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Root,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [Parameter(Mandatory)]
    [ValidateSet('win', 'mac')]
    [string[]] $ExpectedPlatform,
    [string] $WriteList,
    [string] $PythonPath
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validator = Join-Path $repository 'scripts\validate_yak_archives.py'
$resolvedPython = $null
if (-not [string]::IsNullOrWhiteSpace($PythonPath)) {
    if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
        throw "Python was not found at '$PythonPath'."
    }
    $resolvedPython = (Resolve-Path -LiteralPath $PythonPath).Path
} else {
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $pythonCommand) {
        $pythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
    }
    if ($null -ne $pythonCommand) {
        $resolvedPython = $pythonCommand.Source
    }
}
if ([string]::IsNullOrWhiteSpace($resolvedPython)) {
    throw 'Python 3 was not found on PATH. Pass -PythonPath to use an explicit interpreter.'
}

$arguments = @(
    $validator,
    '--root', $Root,
    '--package-name', 'instantmeshplus',
    '--filename-prefix', 'instantmeshplus',
    '--base-version', $Version,
    '--expected-version', $Version,
    '--expected-distribution', 'rh8_32',
    '--expected-guid', 'a8053c38-fdfa-4d99-bc31-b285e4bb59c5'
)
foreach ($platform in $ExpectedPlatform) {
    $arguments += @('--expected-platform', $platform)
}
foreach ($entry in @(
    'manifest.yml',
    'icon.png',
    'InstantMeshPlus.Grasshopper.gha',
    'InstantMeshPlus.Core.dll',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'INSTANT_MESHES_LICENSE.txt',
    'NLOHMANN_JSON_LICENSE.txt',
    'TBB_LICENSE.txt',
    'APACHE-2.0_LICENSE.txt',
    'PCG32_NOTICE.txt',
    'PSS_LICENSE.txt',
    'DSET_LICENSE.txt',
    'EIGEN_COPYING_MPL2.txt',
    'EIGEN_COPYING_README.txt',
    'EIGEN_COPYING_BSD.txt',
    'EIGEN_COPYING_MINPACK.txt',
    'GRASSHOPPER_ASYNC_COMPONENT_LICENSE.txt'
)) {
    $arguments += @('--required-entry', $entry)
}
foreach ($entry in @(
    'win:InstantMeshPlus.Worker.exe',
    'win:tbb12.dll',
    'mac:InstantMeshPlus.Worker',
    'mac:libtbb.12.dylib'
)) {
    $arguments += @('--platform-required-entry', $entry)
}
foreach ($entry in @(
    'win:InstantMeshPlus.Worker',
    'win:libtbb.12.dylib',
    'mac:InstantMeshPlus.Worker.exe',
    'mac:tbb12.dll'
)) {
    $arguments += @('--platform-prohibited-entry', $entry)
}
foreach ($entry in @('mac:InstantMeshPlus.Worker', 'mac:libtbb.12.dylib')) {
    $arguments += @('--platform-executable-entry', $entry)
    $arguments += @('--platform-universal-entry', $entry)
}
if (-not [string]::IsNullOrWhiteSpace($WriteList)) {
    $arguments += @('--write-list', $WriteList)
}

& $resolvedPython @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'Yak archive validation failed.'
}
