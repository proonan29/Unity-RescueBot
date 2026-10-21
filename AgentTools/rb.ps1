# Helper: run a Unity CLI command against the RescueBot editor.
# Usage: .\AgentTools\cf.ps1 <command> [args...]
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:Path += ";C:\Users\proon\AppData\Local\Unity\bin"
Set-Location D:\UnityAIProjs\DigitalTwin\RescueBot
& unity command @args --no-banner 2>&1
