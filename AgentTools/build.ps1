# Compile-check, then refresh Unity and wait until the editor has reloaded. Prints compile errors if any.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
$d = powershell -ExecutionPolicy Bypass -File AgentTools\dryrun.ps1
$d | Where-Object { $_ -match 'error|success=False' } | Select-Object -First 20
if (($d -join "`n") -notmatch 'success=True') { "dryrun failed"; exit 1 }
function Stamp { (Get-ChildItem Library\ScriptAssemblies\Assembly-CSharp*.dll | Measure-Object -Property LastWriteTime -Maximum).Maximum }
$before = Stamp
unity command script run --file AgentTools/Refresh.cs --entry RefreshAssets.Run --result-only --no-banner 2>&1 | Out-Null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 1000
    $now = Stamp
    if ($now -gt $before) {
        $s = unity command editor status --result-only --no-banner 2>&1 | Out-String
        if ($s -match '"status":\s*"ready"' -and $s -match '"compiling":\s*false' -and $s -match '"domainReloadInProgress":\s*false') { "compiled ($($i+1)s)"; exit 0 }
    }
}
"timeout waiting for compile (assembly unchanged?)"
