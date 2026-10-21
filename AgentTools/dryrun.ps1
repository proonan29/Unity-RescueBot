# Compile-check all RescueBot scripts in memory (no domain reload) before letting Unity import them.
# Each file's using directives are moved inside its own namespace block, so files keep their own usings
# (no false "ambiguous Object/Random" errors when everything is combined into one compilation unit).
param([string]$src = "Assets\RescueBot")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
$files = Get-ChildItem $src -Recurse -Filter *.cs
$parts = @()
$pat = '^\s*using\s+[\w\.]+(\s*=\s*[\w\.]+)?\s*;\s*$'
foreach ($f in $files) {
    $lines = Get-Content -Encoding UTF8 $f.FullName
    $usings = @($lines | Where-Object { $_ -match $pat } | ForEach-Object { $_.Trim() })
    $body = @($lines | Where-Object { $_ -notmatch $pat })
    $out = New-Object System.Collections.Generic.List[string]
    $injected = $false
    for ($i = 0; $i -lt $body.Count; $i++) {
        $out.Add($body[$i])
        if (-not $injected -and $body[$i] -match '^\s*namespace\s+[\w\.]+\s*$' -and ($i + 1) -lt $body.Count -and $body[$i + 1].Trim() -eq '{') {
            $out.Add('{'); $i++
            foreach ($u in $usings) { $out.Add($u) }
            $injected = $true
        }
    }
    if (-not $injected) { $out.Insert(0, ($usings -join "`n")) }
    $parts += "// ===== $($f.Name)`n" + ($out -join "`n")
}
$all = ($parts -join "`n") + "`npublic static class __DryRunEntry { public static void Run(){} }"
Set-Content -Encoding utf8 AgentTools\_combined.cs $all
$r = unity command script run --file AgentTools/_combined.cs --entry __DryRunEntry.Run --dry_run true --no-banner --json 2>&1 | Out-String | ConvertFrom-Json
if ($r.errors) { $r.errors | ForEach-Object { "CLI error: $($_.message)" } }
$r.data.result.diagnostics | Where-Object { $_.id -ne "CS0436" } | ForEach-Object { "$($_.severity) $($_.id) L$($_.line): $($_.message) :: $($_.source)" }
"dryrun success=$($r.data.result.success)"
