# Distribution build script.
# Output: publish\ResoPon.exe (single file, self-contained .NET runtime).
# Resonite DLLs are NOT bundled; they are loaded from the installed Resonite at runtime.
param(
    [string]$ResonitePath = "",
    [string]$Output = "$PSScriptRoot\publish"
)

$project = Join-Path $PSScriptRoot "src\VrmToResonitePackage\VrmToResonitePackage.csproj"

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "false",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $Output
)
if ($ResonitePath -ne "") {
    $publishArgs += "-p:ResonitePath=$ResonitePath"
}

& dotnet $publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Output: $Output\ResoPon.exe"
