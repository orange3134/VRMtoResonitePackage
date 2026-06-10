# Distribution build script.
# Output: publish\VrmToResonitePackage.exe (single file, self-contained .NET runtime).
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

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Output: $Output\VrmToResonitePackage.exe"
}
