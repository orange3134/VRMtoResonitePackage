param(
    [Parameter(Mandatory = $true)][string]$CoreSlot,
    [string]$Url,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$cliArguments = @('inspect', $CoreSlot, '--depth', '1', '--components-only', '--members', '--json')
if ($Url) { $cliArguments += @('--url', $Url) }

$raw = & resoloop @cliArguments
if ($LASTEXITCODE -ne 0) { throw "resoloop inspect failed (exit $LASTEXITCODE): $raw" }
$response = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
if (!$response.ok) { throw ($response.error | ConvertTo-Json -Compress -Depth 8) }

$values = @(
    foreach ($entry in $response.data.components) {
        $members = $entry.component.members
        if (!$members.VariableName) { continue }
        $variableName = $members.VariableName.value
        $currentName = $variableName -match '^ExpressionSystem/(State\.(LeftGesture|RightGesture|CurrentExpression)|Config\.(AllowHandGesture\.(Left|Right)|SmoothingSpeed))$'
        $legacyName = $variableName -match '^(ExpressionSystem/(Core\.)?|ExpressionCore/|Expr/)(AllowHandGestures(\.(Left|Right))?|LeftGesture|RightGesture|PairKey|CurrentExpression)$'
        if (!$currentName -and !$legacyName) { continue }
        $member = if ($members.Value) { $members.Value } else { $members.Reference }
        if (!$member) { continue }
        $value = if ($member.kind -eq 'reference') { $member.targetId } else { $member.value }
        [pscustomobject][ordered]@{
            Name = $variableName -replace '^(ExpressionSystem/(Core\.)?|ExpressionCore/|Expr/)', ''
            Value = $value
            Kind = $member.kind
            ComponentId = $entry.component.id
        }
    }
)
$hasCurrentState = ($values.Name -contains 'State.LeftGesture') -and ($values.Name -contains 'State.CurrentExpression')
$hasLegacyState = ($values.Name -contains 'LeftGesture') -and ($values.Name -contains 'CurrentExpression')
if (!$hasCurrentState -and !$hasLegacyState) {
    throw 'The selected slot contains no expression state variables: expected State.LeftGesture and State.CurrentExpression (or legacy equivalents).'
}

if ($Json) {
    [pscustomobject][ordered]@{
        CoreSlot = $CoreSlot
        Values = $values
    } | ConvertTo-Json -Depth 8
} else {
    Write-Output "Core: $CoreSlot"
    $values | Select-Object Name,Value,Kind | Format-Table -AutoSize
}
