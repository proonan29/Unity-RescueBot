# Run a Unity CLI command and print only its (string) result as plain text.
# Usage: .\AgentTools\cfr.ps1 <command> [args...]
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
$raw = & unity command @args --no-banner --result-only 2>&1 | Out-String
try { $j = $raw | ConvertFrom-Json; if ($j -is [string]) { $j } elseif ($j.result -ne $null) { $j.result } else { $raw } } catch { $raw.Substring(0, [Math]::Min(4000, $raw.Length)) }
