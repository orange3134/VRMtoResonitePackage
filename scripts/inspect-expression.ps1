param(
    [Parameter(Mandatory = $true)][string]$CoreSlot,
    [string]$Url,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$cliArguments = @('inspect', $CoreSlot, '--depth', '0', '--components-only', '--members', '--json')
if ($Url) { $cliArguments += @('--url', $Url) }

$raw = & resoloop @cliArguments
if ($LASTEXITCODE -ne 0) { throw "resoloop inspect failed (exit $LASTEXITCODE): $raw" }
$response = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
if (!$response.ok) { throw ($response.error | ConvertTo-Json -Compress -Depth 8) }

$values = @(
    foreach ($entry in $response.data.components) {
        $members = $entry.component.members
        if (!$members.VariableName -or !$members.VariableName.value.StartsWith('Expr/')) { continue }
        $member = if ($members.Value) { $members.Value } else { $members.Reference }
        if (!$member) { continue }
        $value = if ($member.kind -eq 'reference') { $member.targetId } else { $member.value }
        [pscustomobject][ordered]@{
            Name = $members.VariableName.value.Substring(5)
            Value = $value
            Kind = $member.kind
            ComponentId = $entry.component.id
        }
    }
)
if (!($values.Name -contains 'LeftGesture') -or !($values.Name -contains 'CurrentExpression')) {
    throw 'The selected slot is not an expression Core: expected LeftGesture and CurrentExpression.'
}

$status = $values | Where-Object Name -eq 'SelectionStatus' | Select-Object -First 1
$statusNames = @{ 0 = 'Unassigned'; 1 = 'Gesture table'; 2 = 'Direct selection'; 3 = 'Invalid or asset not loaded' }
$selection = if ($status) { $statusNames[[int]$status.Value] } else { 'Not available in this package version' }

if ($Json) {
    [pscustomobject][ordered]@{
        CoreSlot = $CoreSlot
        Selection = $selection
        Values = $values
    } | ConvertTo-Json -Depth 8
} else {
    Write-Output "Core: $CoreSlot"
    Write-Output "Selection: $selection"
    $values | Select-Object Name,Value,Kind | Format-Table -AutoSize
}
