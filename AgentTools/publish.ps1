$ErrorActionPreference = 'Continue'
Set-Location 'D:\UnityAIProjs\DigitalTwin\RescueBot'
git add Docs/BuildFromScratch.md
$m = "RescueBot: add BuildFromScratch docs`n`nCo-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01JNuRpkomNVu2BjCvUzs5iB`n"
$f = 'D:\UnityAIProjs\DigitalTwin\RescueBot\.git\COMMITMSG'
[IO.File]::WriteAllText($f, $m, (New-Object Text.UTF8Encoding($false)))
git commit -F $f
git log --oneline | Select-Object -First 3

$cred = "protocol=https`nhost=github.com`n`n" | git credential fill
$tok = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=',''
if (-not $tok) { 'NO TOKEN'; exit 1 }
$h = @{ Authorization = "token $tok"; 'User-Agent' = 'rb'; Accept = 'application/vnd.github+json' }
$user = (Invoke-RestMethod -Uri https://api.github.com/user -Headers $h).login
"user=$user"
try {
  $body = @{ name = 'Unity-RescueBot'; description = 'Vision-guided rescue rover simulation in a disaster-struck city (Unity 6, camera-only navigation)'; private = $false } | ConvertTo-Json
  $r = Invoke-RestMethod -Method Post -Uri https://api.github.com/user/repos -Headers $h -Body $body -ContentType 'application/json'
  "created $($r.html_url)"
} catch { "create: $($_.Exception.Message)" }
git remote remove origin 2>$null
git remote add origin "https://github.com/$user/Unity-RescueBot.git"
git branch -M main
git push -u origin main 2>&1
git status -sb | Select-Object -First 3
