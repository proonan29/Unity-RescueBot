# Training set (seeds 101..148, lighting randomised per seed) + validation sets (seeds 901..906): val = randomised lighting, val_std = the standard scene lighting.
# Cities never overlap the evaluation seeds 1..5.
$root = 'D:\UnityAIProjs\DigitalTwin\RescueBot'
Set-Location $root
foreach ($d in 'Dataset', 'Dataset_test', 'Dataset_dbg') { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
$sizes = 3,4,5,6,7,8
$spacings = 40,60,80,100
function Gen($seed, $out, $light) {
    $nx = $sizes[$seed % 6]; $ny = $sizes[($seed * 7 + 3) % 6]
    $sev = 0.3 + (($seed * 13) % 7) * 0.1
    $sp = $spacings[$seed % 4]
    $r = & "$root\AgentTools\rbr.ps1" rb_dump_dataset --seed $seed --nx $nx --ny $ny --severity $sev --spacing $sp --drive 140 --rand 160 --out $out --light $light
    "$seed $out : $($r | Select-Object -First 1)"
}
foreach ($s in 901..906) { Gen $s 'Dataset/val' 1; Gen $s 'Dataset/val_std' 0 }
foreach ($s in 101..148) { Gen $s 'Dataset/train' 1 }
'DONE'
