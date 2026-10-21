# Classic vs CNN on identical cities: arrival rate, efficiency, collisions. Usage: cmp.ps1 -sev 0.5 -seeds "1,2,3" [-trips 8] [-nx 5] [-ny 5]
param([double]$sev = 0.5, [string]$seeds = '1,2,3', [int]$trips = 8, [int]$nx = 5, [int]$ny = 5, [string]$out = 'D:\UnityAIProjs\DigitalTwin\RescueBot\Tools\cnn\compare_log.txt')
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
foreach ($s in ($seeds -split ',')) {
    foreach ($p in 'classic', 'cnn') {
        $raw = unity command rb_sim_run --timeout 240 --seed $s --trips $trips --severity $sev --nx $nx --ny $ny --max_seconds 200 --perception $p --result-only --no-banner 2>&1 | Out-String
        try { $txt = $raw | ConvertFrom-Json } catch { $txt = $raw }
        $keep = ($txt -split "`n" | Where-Object { $_ -match '^totals|^wall|^perception:' } | ForEach-Object { $_.Trim() }) -join ' | '
        $line = "seed=$s sev=$sev ${nx}x${ny} trips=$trips $p :: $keep"
        $line
        Add-Content -Path $out -Value $line
    }
}
'DONE'
