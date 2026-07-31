[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $YakPath,
    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string] $Source,
    [Parameter(Mandatory)]
    [string] $PackageName,
    [Parameter(Mandatory)]
    [string] $Version,
    [switch] $ShouldExist,
    [ValidateRange(1, 20)]
    [int] $RetryCount = 1,
    [ValidateRange(0, 120)]
    [int] $DelaySeconds = 0
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $YakPath -PathType Leaf)) {
    throw "Yak CLI was not found at '$YakPath'."
}
$resolvedYakPath = (Resolve-Path -LiteralPath $YakPath).Path
$escapedName = [regex]::Escape($PackageName)
$linePattern = "(?im)^\s*$escapedName\s+\((?<versions>[^)]*)\)\s*$"

for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
    $searchOutput = @(
        & $resolvedYakPath search --all --prerelease "--source=$Source" $PackageName 2>&1 |
            ForEach-Object { $_.ToString() }
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Yak search failed for '$PackageName' at '$Source': $($searchOutput -join [Environment]::NewLine)"
    }

    $plainOutput = ($searchOutput -join [Environment]::NewLine) -replace '\x1B\[[0-?]*[ -/]*[@-~]', ''
    $matches = [regex]::Matches($plainOutput, $linePattern)
    $versions = @(
        foreach ($match in $matches) {
            $match.Groups['versions'].Value.Split(',') |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        }
    )
    $found = $versions -contains $Version

    if (-not $ShouldExist) {
        if ($found) {
            throw "Yak package '$PackageName' version '$Version' already exists at '$Source'."
        }
        Write-Output "Yak package '$PackageName' version '$Version' is not present at '$Source'."
        return
    }

    if ($found) {
        Write-Output "Verified Yak package '$PackageName' version '$Version' at '$Source'."
        return
    }

    if ($attempt -lt $RetryCount) {
        Start-Sleep -Seconds $DelaySeconds
    }
}

throw "Yak package '$PackageName' version '$Version' was not found at '$Source' after $RetryCount attempt(s)."
