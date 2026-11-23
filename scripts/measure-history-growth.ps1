#requires -Version 7
<#
.SYNOPSIS
Read-only report of how much local state and history the monitor stores.

.DESCRIPTION
Counts retained deltas in state.json and the newest daily history files, the share of zero-amount deltas, and the
bytes used per service and bucket. Nothing is modified and no account identifiers are printed.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'AiUsageTray'),
    [ValidateRange(1, 90)][int]$Days = 3
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes), [System.Text.Json.JsonDocumentOptions]::new())
}

function Add-Entry($Table, [System.Text.Json.JsonElement]$Key, [System.Text.Json.JsonElement]$Delta, [int]$Bytes) {
    $name = $Key.GetProperty('Account').GetProperty('Provider').GetString() + '/' + $Key.GetProperty('Bucket').GetString()
    if (-not $Table.ContainsKey($name)) { $Table[$name] = [pscustomobject]@{ Bucket = $name; Records = 0; ZeroAmount = 0; Bytes = 0L } }
    $row = $Table[$name]
    $row.Records++
    $row.Bytes += $Bytes
    $amount = $Delta.GetProperty('Amount')
    if ($amount.ValueKind -eq 'Number' -and $amount.GetDecimal() -eq 0) { $row.ZeroAmount++ }
}

function Show-Table([string]$Title, $Table) {
    Write-Output $Title
    $total = ($Table.Values | Measure-Object Bytes -Sum).Sum
    $Table.Values | Sort-Object Bytes -Descending | ForEach-Object {
        [pscustomobject]@{
            Bucket     = $_.Bucket
            Records    = $_.Records
            ZeroAmount = '{0:P0}' -f ($_.ZeroAmount / [Math]::Max(1, $_.Records))
            MegaBytes  = [Math]::Round($_.Bytes / 1MB, 2)
            ShareOfAll = '{0:P0}' -f ($_.Bytes / [Math]::Max(1, $total))
        }
    } | Format-Table -AutoSize | Out-String | Write-Output
}

$statePath = Join-Path $DataRoot 'state.json'
if (-not (Test-Path -LiteralPath $statePath)) { throw "state.json was not found under $DataRoot" }
$stateFile = Get-Item -LiteralPath $statePath
"state.json: {0:N1} MB, last written {1}" -f ($stateFile.Length / 1MB), $stateFile.LastWriteTime
$state = Read-Json $statePath
try {
    $table = @{}
    foreach ($delta in $state.RootElement.GetProperty('Deltas').EnumerateArray()) {
        Add-Entry $table $delta.GetProperty('Key') $delta $delta.GetRawText().Length
    }
    $count = ($table.Values | Measure-Object Records -Sum).Sum
    "Retained deltas in state.json: {0:N0}" -f $count
    Show-Table 'Retained deltas by bucket' $table
}
finally { $state.Dispose() }

$historyRoot = Join-Path $DataRoot 'history'
$files = @(Get-ChildItem -LiteralPath $historyRoot -Recurse -File -Filter '????-??-??.json' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First $Days)
if ($files.Count -eq 0) { Write-Output 'No daily history files were found.'; return }
$table = @{}
foreach ($file in $files) {
    $document = Read-Json $file.FullName
    try {
        foreach ($record in $document.RootElement.GetProperty('Records').EnumerateArray()) {
            Add-Entry $table $record.GetProperty('Snapshot').GetProperty('Key') $record.GetProperty('Delta') $record.GetRawText().Length
        }
    }
    finally { $document.Dispose() }
}
"Newest {0} daily history file(s): {1:N1} MB on disk" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)
Show-Table 'Daily history records by bucket' $table
