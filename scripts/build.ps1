[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$preset = if ($Configuration -eq 'Release') { 'windows-x64-release' } else { 'windows-x64-debug' }
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($null -eq $cmakeCommand) {
    $visualStudioCMake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path -LiteralPath $visualStudioCMake)) {
        throw 'CMake was not found on PATH or in the Visual Studio 2022 Community installation.'
    }
    $cmake = $visualStudioCMake
} else {
    $cmake = $cmakeCommand.Source
}
$ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
if (-not (Test-Path -LiteralPath $ctest -PathType Leaf)) {
    throw "CTest was not found beside CMake at '$ctest'."
}

Push-Location $repository
try {
    & $cmake --preset $preset
    if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }
    & $cmake --build --preset $preset --target InstantMeshPlus.Worker
    if ($LASTEXITCODE -ne 0) { throw 'Native worker build failed.' }
    & $ctest --preset $preset
    if ($LASTEXITCODE -ne 0) { throw 'Native worker smoke test failed.' }
    dotnet build .\InstantMeshPlus.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }
    dotnet test .\tests\InstantMeshPlus.Core.Tests\InstantMeshPlus.Core.Tests.csproj -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    Pop-Location
}
