# Publishes the backend as a self-contained app (no .NET install needed) into the extension's server/<rid>/ folder,
# where the extension starts it from. Default: this machine's platform. Example:
#   ./scripts/publish-backend.ps1                 # win-x64
#   ./scripts/publish-backend.ps1 -Rid linux-x64
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "ai-chat-extension/server/$Rid"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root "Ai-Agent/Ai-Agent/Ai-Agent.csproj") `
    -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Backend published to $out"
