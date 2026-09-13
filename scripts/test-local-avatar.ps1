param(
    [string]$CaseId,
    [switch]$List,
    [ValidateSet('Convert', 'Dump')][string]$Mode = 'Convert',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$registryPath = Join-Path $repoRoot '.local/avatar-tests.json'
if (!(Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "No local cases registered. See docs/local-avatar-tests.md: $registryPath"
}
$registry = Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($registry.version -ne 1) { throw 'Unsupported local case registry version.' }
if ($List) {
    $registry.cases | Select-Object id, tags, inputPath, @{Name='Exists';Expression={
        Test-Path -LiteralPath $_.inputPath -PathType Leaf
    }} | Format-List
    exit 0
}
if ($CaseId -notmatch '^[a-z0-9][a-z0-9_-]*$') { throw 'Specify a valid -CaseId (or use -List).' }
$cases = @($registry.cases | Where-Object { $_.id -eq $CaseId })
if ($cases.Count -ne 1) { throw "Expected one registered case: $CaseId" }
$case = $cases[0]
if (!(Test-Path -LiteralPath $case.inputPath -PathType Leaf)) { throw "Input not found: $($case.inputPath)" }
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $repoRoot ".tmp_verify/local-avatar-tests/$CaseId/$runId"
$latestDirectory = Join-Path $repoRoot '.local/avatar-test-results'
New-Item -ItemType Directory -Force -Path $runDirectory, $latestDirectory | Out-Null
$result = [ordered]@{
    caseId = $CaseId; mode = $Mode; startedUtc = [DateTime]::UtcNow.ToString('o')
    inputPath = $case.inputPath; inputSha256 = (Get-FileHash -LiteralPath $case.inputPath -Algorithm SHA256).Hash
    gitHead = $null; hasUncommittedChanges = $null; skippedBuild = [bool]$SkipBuild
    runDirectory = $runDirectory; commands = @(); status = 'running'; error = $null
}
function Invoke-Logged([string]$File, [string[]]$CommandArguments, [string]$LogName) {
    $logPath = Join-Path $runDirectory $LogName
    Write-Host "Running $LogName (log: $logPath)"
    # PowerShell 5.1 treats native stderr as ErrorRecord. Preserve it in the log
    # and use the native exit code to decide success.
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $File @CommandArguments > $logPath 2>&1
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $savedPreference }
    $result.commands += [ordered]@{ file = $File; arguments = $CommandArguments; log = $logPath; exitCode = $code }
    if ($code -ne 0) { throw "$LogName failed with exit code $code. See $logPath" }
}
$oldNoPause = $env:RESOPON_NOPAUSE
Push-Location $repoRoot
try {
    $result.gitHead = git rev-parse HEAD
    $result.hasUncommittedChanges = [bool](git status --porcelain --untracked-files=normal)
    $env:RESOPON_NOPAUSE = '1'
    $dll = Join-Path $repoRoot 'src/VrmToResonitePackage/bin/Release/ResoPon.dll'
    if (!$SkipBuild) {
        $buildDirectory = Join-Path $runDirectory 'build'
        Invoke-Logged 'dotnet' @('build', 'src/VrmToResonitePackage', '-c', 'Release', '-o', $buildDirectory) 'build.log'
        $dll = Join-Path $buildDirectory 'ResoPon.dll'
    }
    if (!(Test-Path -LiteralPath $dll)) { throw "Build not found: $dll" }
    $result.converterSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    $extra = @($case.arguments | Where-Object { $null -ne $_ })
    if ($Mode -eq 'Dump') {
        $flag = if ([IO.Path]::GetExtension($case.inputPath) -eq '.vrm') { '--assimp-dump' } else { '--vrchat-dump' }
        Invoke-Logged 'dotnet' (@($dll, $flag, $case.inputPath) + $extra) 'dump.log'
    } else {
        $outputDirectory = Join-Path $runDirectory 'output'
        Invoke-Logged 'dotnet' (@($dll, $case.inputPath) + $extra + @('--output', $outputDirectory)) 'convert.log'
        $packages = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.resonitepackage' -File)
        if ($packages.Count -ne 1) { throw "Expected one output package, found $($packages.Count)." }
        $result.outputPackage = $packages[0].FullName
        Invoke-Logged 'dotnet' @($dll, '--inspect', $packages[0].FullName) 'inspect.log'
    }
    $result.status = 'passed'
} catch {
    $result.status = 'failed'
    $result.error = $_.Exception.Message
    Write-Host $result.error
} finally {
    $env:RESOPON_NOPAUSE = $oldNoPause
    Pop-Location
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $json = $result | ConvertTo-Json -Depth 8
    $json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json') -Encoding UTF8
    $json | Set-Content -LiteralPath (Join-Path $latestDirectory "$CaseId.$Mode.json") -Encoding UTF8
    Write-Host "Result: $($result.status) - $runDirectory"
}
if ($result.status -ne 'passed') { exit 1 }
