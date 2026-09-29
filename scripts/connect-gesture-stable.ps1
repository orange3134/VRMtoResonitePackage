param(
    [Parameter(Mandatory=$true)][string]$Snapshot,
    [Parameter(Mandatory=$true)][string]$Plan,
    [string]$Url,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$observed = Get-Content -LiteralPath $Snapshot -Raw | ConvertFrom-Json
if (!$observed.ok) { throw 'Snapshot must be a successful resoloop inspect response.' }
$nodes = [Collections.Generic.List[object]]::new()
function Collect($slot, $insideStable) {
    $insideStable = $insideStable -or $slot.name -eq 'GestureStable'
    foreach ($c in $slot.components) { $nodes.Add([pscustomobject]@{slot=$slot.name; stable=$insideStable; component=$c}) }
    foreach ($child in $slot.children) { Collect $child $insideStable }
}
Collect $observed.data $false
function One($items, $description) {
    $items = @($items)
    if ($items.Count -ne 1) { throw "Expected exactly one $description, found $($items.Count)." }
    $items[0].component
}
$candidate = One ($nodes | Where-Object { !$_.stable -and $_.slot -like '* : Candidate' -and $_.component.type -match 'StoredValue<int>' }) 'Candidate'
$accept = One ($nodes | Where-Object { !$_.stable -and $_.slot -eq '077 If' }) 'acceptance branch'
$writeCandidate = One ($nodes | Where-Object { !$_.stable -and $_.slot -eq '007 ValueWrite<Int32>' }) 'candidate writer'
$resetCandidate = One ($nodes | Where-Object { !$_.stable -and $_.slot -eq '070 ValueWrite<Int32>' }) 'candidate reset'
$sender = One ($nodes | Where-Object { !$_.stable -and $_.component.type -match 'DynamicImpulseTriggerWithValue<int>' }) 'sender'
$watch = One ($nodes | Where-Object { !$_.stable -and $_.component.type -match 'FireOnLocalValueChange<bool>' }) 'ready change detector'
$ready = One ($nodes | Where-Object { $_.component.id -eq $watch.members.OnChange.targetId -and $_.component.type -match '\.If$' }) 'ready true branch'
$stable = One ($nodes | Where-Object { $_.stable -and $_.component.type -match 'StoredValue<int>' }) 'Stable'
$check = One ($nodes | Where-Object { $_.stable -and $_.slot -eq 'Check' -and $_.component.type -match '\.If$' }) 'stable guard'
$remember = One ($nodes | Where-Object { $_.stable -and $_.slot -eq 'Remember' -and $_.component.type -match '\.Sequence$' }) 'remember sequence'
$reset = One ($nodes | Where-Object { $_.stable -and $_.slot -eq 'Reset' -and $_.component.type -match '\.Sequence$' }) 'stable reset sequence'
$compare = One ($nodes | Where-Object { $_.stable -and $_.component.type -match 'ValueNotEquals<int>' }) 'stable comparison'
$writeStable = One ($nodes | Where-Object { $_.stable -and $_.component.type -match 'ValueWrite<int>' -and $_.component.members.Variable.targetId -eq $stable.id -and $_.component.members.Value.targetId -ne ($nodes | Where-Object { $_.stable -and $_.component.type -match 'ValueInput<int>' }).component.id }) 'stable remember writer'
$changes = [Collections.Generic.List[object]]::new()
function Link($component, $member, $target, $reason) {
    $field = $component.members.$member
    if (!$field -or $field.kind -ne 'reference') { throw "Missing reference member $member on $($component.id)." }
    $changes.Add([pscustomobject]@{component=$component.id; type=$component.type; member=$member; before=$field.targetId; after=$target; reason=$reason})
}
Link $compare 'A' $candidate.id 'Compare the current candidate with last sent Stable.'
Link $writeStable 'Value' $candidate.id 'Remember the actual sent candidate.'
Link $check 'OnTrue' $sender.id 'Only first or changed stable values reach the existing sender.'
Link $sender 'Next' $remember.id 'Record Stable and HasStable after sending.'
Link $resetCandidate 'OnWritten' $reset.id 'Clear last-sent state whenever input is unavailable.'
Link $ready 'OnTrue' $check.id 'Keep the existing true-only ready guard, then suppress duplicates.'
Link $accept 'OnTrue' $writeCandidate.id 'Always capture the newest candidate and restart its timer.'
$changes | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Plan -Encoding UTF8
$changes | Select-Object member,before,after,reason | Format-Table -Wrap
if (!$Apply) { return }
if (!$Url) { throw 'Apply requires an explicit world URL.' }
foreach ($change in $changes) {
    $fresh = resoloop component inspect $change.component --url $Url --json | ConvertFrom-Json
    if (!$fresh.ok -or $fresh.data.type -ne $change.type) { throw "Cannot verify type of $($change.component)." }
    $current = $fresh.data.members.($change.member).targetId
    if ($current -eq $change.after) { Write-Output "Unchanged: $($change.reason)"; continue }
    if ($current -ne $change.before) { throw "Concurrent edit at $($change.component).$($change.member); inspect and re-plan." }
    $result = resoloop component set $change.component $change.member $change.after --url $Url --json | ConvertFrom-Json
    if (!$result.ok) { throw ($result | ConvertTo-Json -Depth 12) }
    Write-Output "Updated: $($change.reason)"
}
