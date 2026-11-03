[CmdletBinding()]
param([switch]$RemoveNewEmptyResidue)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class VoiceImeRegistryTime {
    [DllImport("advapi32.dll", EntryPoint="RegQueryInfoKeyW")]
    public static extern int Query(SafeRegistryHandle key, IntPtr cls, IntPtr clsLength, IntPtr reserved,
        IntPtr subkeys, IntPtr maxSubkey, IntPtr maxClass, IntPtr values, IntPtr maxValueName,
        IntPtr maxValue, IntPtr security, out long lastWrite);
}
'@
$clsid = '{5638E6E5-D18C-4FE8-B778-E59A5170B481}'
$path = "Software\Classes\VirtualStore\MACHINE\SOFTWARE\Microsoft\CTF\TIP\$clsid"
$from = [DateTimeOffset]::Parse('2026-11-02T12:01:43Z').UtcDateTime
$to = [DateTimeOffset]::Parse('2026-11-02T12:02:10Z').UtcDateTime
$rows = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ FromUtc = $from.ToString('o'); ToUtc = $to.ToString('o'); Removed = @() }
$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
function Read-Node([string]$name) {
    $key = $base.OpenSubKey($name, $false)
    if ($null -eq $key) { return }
    try {
        [long]$time = 0
        $errorCode = [VoiceImeRegistryTime]::Query($key.Handle, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero,
            [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$time)
        if ($errorCode -ne 0) { throw "RegQueryInfoKey=$errorCode" }
        $utc = [DateTime]::FromFileTimeUtc($time)
        $rows.Add([ordered]@{ Path = $name; ValueCount = $key.ValueCount; LastWriteUtc = $utc.ToString('o'); InTestWindow = ($utc -ge $from -and $utc -le $to) })
        foreach ($child in $key.GetSubKeyNames()) { Read-Node ($name + '\' + $child) }
    } finally { $key.Dispose() }
}
try {
    Read-Node $path
    $report.Nodes = @($rows)
    $report.Eligible = $rows.Count -gt 0
    foreach ($node in $rows) {
        if ($node.ValueCount -ne 0 -or -not $node.InTestWindow) { $report.Eligible = $false }
        Write-Output "$($node.Path) values=$($node.ValueCount) utc=$($node.LastWriteUtc) in-window=$($node.InTestWindow)"
    }
    if ($RemoveNewEmptyResidue) {
        if (-not $report.Eligible) { throw '不是本次试验时间窗内的全空目录；拒绝删除。' }
        foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
            foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
                $check = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                try {
                    foreach ($normal in @("Software\Classes\CLSID\$clsid", "Software\Microsoft\CTF\TIP\$clsid")) {
                        $key = $check.OpenSubKey($normal, $false)
                        if ($key) { $key.Dispose(); throw '正常注册存在；拒绝删除。' }
                    }
                } finally { $check.Dispose() }
            }
        }
        for ($i = $rows.Count - 1; $i -ge 0; --$i) {
            $node = $rows[$i] # Reverse preorder: children before parents, no dictionary pipeline enumeration.
            # Recheck each leaf and never recurse-delete data or delete any ancestor outside this CLSID.
            $key = $base.OpenSubKey($node.Path, $false)
            try { if ($key.ValueCount -ne 0 -or $key.SubKeyCount -ne 0) { throw '节点不再为空；停止清理。' } }
            finally { if ($key) { $key.Dispose() } }
            $base.DeleteSubKey($node.Path, $false)
            $report.Removed += $node.Path
        }
    }
    $remaining = $base.OpenSubKey($path, $false)
    $report.ExistsAfter = $null -ne $remaining
    if ($remaining) { $remaining.Dispose() }
    Write-Output "ELIGIBLE=$($report.Eligible) REMOVED=$($report.Removed.Count) EXISTS_AFTER=$($report.ExistsAfter)"
} finally {
    $base.Dispose()
    $suffix = if ($RemoveNewEmptyResidue) { 'cleanup' } else { 'audit' }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $PSScriptRoot "artifacts\virtual-residue-$suffix.json") -Encoding UTF8
}