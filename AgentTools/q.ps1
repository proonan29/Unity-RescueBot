$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
if (-not (Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -or $_.Path -like '*Editor*' })) {
    Start-Process 'C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe' -ArgumentList '-projectPath', 'D:\UnityAIProjs\DigitalTwin\RescueBot'
    'launched'
}
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 4
    $s = unity command editor status --result-only --no-banner 2>&1 | Out-String
    if ($s -match '"status":\s*"ready"') { "ready after $($i * 4)s"; break }
}
$s
