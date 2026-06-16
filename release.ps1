# Local release script.
# Builds with publish.ps1 and uploads only the EXE to a GitHub Release.
param(
    [string]$Tag = "",
    [string]$ResonitePath = "",
    [string]$Output = "$PSScriptRoot\publish",
    [string]$AssetName = "ResoPon.exe",
    [switch]$CreateTag,
    [switch]$PushTag
)

$ErrorActionPreference = "Stop"
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($null -eq $gh) {
    throw "GitHub CLI 'gh' was not found. Install it and run 'gh auth login' first."
}

$publishScript = Join-Path $PSScriptRoot "publish.ps1"
if (-not (Test-Path $publishScript)) {
    throw "publish.ps1 was not found: $publishScript"
}

if ($ResonitePath -ne "") {
    & $publishScript -ResonitePath $ResonitePath -Output $Output
} else {
    & $publishScript -Output $Output
}
if ($LASTEXITCODE -ne 0) {
    throw "publish.ps1 failed with exit code $LASTEXITCODE."
}

$exePath = Join-Path $Output $AssetName
if (-not (Test-Path $exePath)) {
    throw "Release asset was not found: $exePath"
}

function Get-ReleaseVersionFromAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $Path))
    $version = $versionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = $versionInfo.FileVersion
    }
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Could not read a version from release asset: $Path"
    }

    $metadataIndex = $version.IndexOf("+")
    if ($metadataIndex -ge 0) {
        $version = $version.Substring(0, $metadataIndex)
    }

    return $version.Trim()
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $version = Get-ReleaseVersionFromAsset -Path $exePath
    $Tag = "v$version"
    Write-Host "Release tag: $Tag"
}

if ($CreateTag) {
    git tag $Tag
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create git tag: $Tag"
    }
}

if ($PushTag) {
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push git tag: $Tag"
    }
}

$releaseTitle = "ResoPon $Tag"

$releaseExists = $false
try {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    gh release view $Tag *> $null
    if ($LASTEXITCODE -eq 0) {
        $releaseExists = $true
    }
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($releaseExists) {
    gh release upload $Tag $exePath --clobber
} else {
    gh release create $Tag $exePath --title $releaseTitle --notes "Release $Tag."
}

if ($LASTEXITCODE -ne 0) {
    throw "Failed to upload GitHub Release asset."
}

Write-Host ""
Write-Host "Uploaded: $exePath"
