$f = 'D:\UnityAIProjs\DigitalTwin\RescueBot\Assets\RescueBot\Scripts\Core\CnnSegmenter.cs'
"md5=$((Get-FileHash $f -Algorithm MD5).Hash.ToLower())"
$o = & D:\UnityAIProjs\DigitalTwin\RescueBot\AgentTools\q2.ps1
$o | Select-Object -First 4
& D:\UnityAIProjs\DigitalTwin\RescueBot\AgentTools\rbr.ps1 rb_sim_run --seed 1 --trips 1 --severity 0.5 --perception cnn --max_seconds 10
