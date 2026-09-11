param(
    [string]$Base = 'main',
    [string]$CodexPath = 'codex',
    [switch]$Fix,
    [switch]$CommitFixes,
    [string]$ResumeReport,
    [ValidateRange(1, 100)][int]$MaxRounds = 10,
    [ValidateRange(1, 10)][int]$CleanPasses = 1
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repo
    $codexCommand = Get-Command $CodexPath -ErrorAction Stop
    if ($CommitFixes) {
        if (!$Fix) { throw '-CommitFixes requires -Fix.' }
        $initialStatus = & git status --porcelain
        if ($LASTEXITCODE -ne 0 -or $initialStatus) {
            throw '-CommitFixes requires a clean working tree to avoid committing pre-existing changes.'
        }
    }
    if ($ResumeReport) { $ResumeReport = (Resolve-Path -LiteralPath $ResumeReport).Path }
    $baseCommit = & git merge-base HEAD $Base
    if ($LASTEXITCODE -ne 0) { throw "Cannot resolve review base: $Base" }
    $run = Join-Path $repo ('.tmp_verify/local-review/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    Write-Host "Review artifacts: $run"
    $clean = 0
    for ($round = 1; $round -le $MaxRounds; $round++) {
        Write-Host "Round $round : build and parser regression checks"
        & dotnet build src/VrmToResonitePackage -c Release *> (Join-Path $run "$round-build.log")
        if ($LASTEXITCODE -ne 0) { throw "Build failed. See $run" }
        & dotnet run --project tests/PrefabInputSmoke -c Release *> (Join-Path $run "$round-tests.log")
        if ($LASTEXITCODE -ne 0) { throw "Parser regression checks failed. See $run" }
        & git diff --check
        if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }

        $report = Join-Path $run "$round-review.json"
        $prompt = @"
Review this repository's full PR changes relative to merge base $baseCommit, including staged,
unstaged and untracked source files. Read CLAUDE.md and relevant implementation docs. Inspect
git diff $baseCommit and git ls-files --others --exclude-standard. Do not enumerate ignored
fixtures or artifacts. Build and parser checks just passed; their logs are in $run.
Review correctness, regressions and missing edge cases, following callers through conversion
and scene setup rather than stopping at parser data. Report only actionable defects introduced
by the changes, with a concrete trigger, impact and file/line. Do not limit finding count.
Do not invent findings, request stylistic changes or repeat issues that are already fixed.
Do not edit files, call GitHub, post comments, commit, push or invoke another review process.
Return completed=false if you could not finish reviewing; an empty findings list is success
only if the review completed. Return the requested JSON object.
"@
        Write-Host "Round $round : independent local review"
        if ($round -eq 1 -and $ResumeReport) {
            Copy-Item -LiteralPath $ResumeReport -Destination $report
        } else {
        # Windows PowerShell 5.1 promotes native stderr warnings to ErrorRecords.
        # Native exit status, not a diagnostic on stderr, determines success.
        $ErrorActionPreference = 'Continue'
        $prompt | & $codexCommand.Source exec --ephemeral -s read-only --color never --json `
            --output-schema (Join-Path $PSScriptRoot 'review-schema.json') -o $report - `
            *> (Join-Path $run "$round-review.log")
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "Review failed. See $run" }
        }
        $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($result.completed -ne $true -or $null -eq $result.findings) { throw "Review incomplete. See $report" }
        if ($result.findings.Count -eq 0) {
            $clean++
            Write-Host "No findings ($clean/$CleanPasses consecutive completed reviews)."
            if ($clean -ge $CleanPasses) { return }
            continue
        }
        $clean = 0
        $result.findings | Format-Table priority, file, line, title -AutoSize
        if (!$Fix) { throw "Review has findings. See $report; rerun with -Fix to apply and re-review." }
        $fixPrompt = @"
Fix the actionable findings in $report in this working tree. Read CLAUDE.md and relevant docs.
Reproduce each defect and add focused regression coverage where appropriate. Preserve existing
user changes. Run the Release build and PrefabInputSmoke, correcting any failures. If a finding
is invalid, explain the concrete evidence in your final report instead of making a needless edit.
Do not call GitHub, post comments, commit, push, invoke review-local.ps1 or launch another reviewer.
The parent script will run a fresh review of the full PR after you finish.
Return the requested JSON object. In commit_message, write a concise imperative subject that
describes the concrete behavior fixed, followed by a blank line and explanatory body when useful.
Name the affected behavior and outcome; do not use generic subjects such as "Fix review findings"
or a review round number. Summarize verification and any invalid findings in summary.
"@
        Write-Host "Round $round : apply fixes"
        $ErrorActionPreference = 'Continue'
        $fixPrompt | & $codexCommand.Source exec --ephemeral --approve-for-me --color never --json `
            --output-schema (Join-Path $PSScriptRoot 'fix-schema.json') `
            -o (Join-Path $run "$round-fixes.json") - *> (Join-Path $run "$round-fixes.log")
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "Fix execution failed. See $run" }
        if ($CommitFixes) {
            Write-Host "Round $round : verify fixes before committing"
            & dotnet build src/VrmToResonitePackage -c Release *> (Join-Path $run "$round-fixed-build.log")
            if ($LASTEXITCODE -ne 0) { throw "Fixed build failed. See $run" }
            & dotnet run --project tests/PrefabInputSmoke -c Release *> (Join-Path $run "$round-fixed-tests.log")
            if ($LASTEXITCODE -ne 0) { throw "Fixed regression checks failed. See $run" }
            & git diff --check
            if ($LASTEXITCODE -ne 0) { throw 'Fixed whitespace check failed.' }
            $status = & git status --porcelain
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect changes for commit.' }
            if ($status) {
                $fixResult = Get-Content -LiteralPath (Join-Path $run "$round-fixes.json") -Raw -Encoding UTF8 | ConvertFrom-Json
                $commitMessage = $fixResult.commit_message
                if ($commitMessage -isnot [string] -or [string]::IsNullOrWhiteSpace($commitMessage)) {
                    throw 'Fix execution did not provide a descriptive commit message.'
                }
                $messageFile = Join-Path $run "$round-commit-message.txt"
                [IO.File]::WriteAllText($messageFile, $commitMessage.Trim() + "`n", (New-Object Text.UTF8Encoding($false)))
                & git add --all
                if ($LASTEXITCODE -ne 0) { throw 'Could not stage fixes.' }
                & git commit --file $messageFile
                if ($LASTEXITCODE -ne 0) { throw 'Could not commit fixes.' }
            }
        }
    }
    throw "Review did not reach $CleanPasses clean passes within $MaxRounds rounds. See $run"
} finally {
    Set-Location -LiteralPath $originalLocation
}
