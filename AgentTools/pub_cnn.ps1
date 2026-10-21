Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
$gi = Get-Content .gitignore -Raw
if ($gi -notmatch '/Dataset/') { Add-Content .gitignore "`n/Dataset/`n/Dataset_*/" }
$msg = @"
CNN(U-Net) 영상 인식 추가: Inference Engine 연동, UI/CLI 전환, 고전 대비 평가

- Perception에 ISegmenter 시임, CnnSegmenter(rescue_seg.onnx, ~2.4ms/frame)
- 학습 데이터/학습 스크립트(Tools/cnn), 조명 독립 하네스, 라벨 렌더링
- 비교: 시드 1-3, 도착 21/24 -> 23/24, 충돌 23 -> 13, 어두운 조명에서 정확도 43% -> 99.6%
- Docs/CNN.md 추가, Dataset/ gitignore

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01JNuRpkomNVu2BjCvUzs5iB
"@
[IO.File]::WriteAllText("$PWD\AgentTools\msg.txt", $msg, (New-Object Text.UTF8Encoding($false)))
git add -A
git status --short | Select-Object -First 40
git commit -F AgentTools\msg.txt
git push origin HEAD 2>&1
git log --oneline -3
