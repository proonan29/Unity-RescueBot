$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
unity command script run --file AgentTools/Refresh.cs --entry RefreshAssets.Run --result-only --no-banner 2>&1 | Out-Null
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Seconds 3
    $s = unity command editor status --result-only --no-banner 2>&1 | Out-String
    if ($i -gt 2 -and $s -match '"compiling":\s*false' -and $s -match '"domainReloadInProgress":\s*false') { "settled after $($i * 3)s"; break }
}
Get-ChildItem 'Library\ScriptAssemblies\Assembly-CSharp*.dll' | ForEach-Object { $_.Name + ' ' + $_.LastWriteTime.ToString('HH:mm:ss') }
"now " + (Get-Date).ToString('HH:mm:ss')
unity command script run --file AgentTools/ConsoleDump.cs --entry ConsoleDump.Run --result-only --no-banner 2>&1 | Out-String
