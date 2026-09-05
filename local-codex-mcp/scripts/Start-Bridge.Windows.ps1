[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AllowedRoot,

    [string] $DefaultCwd = $AllowedRoot,

    [string] $CodexCommand = '',

    [string] $NodeCommand = 'node'
)

$ErrorActionPreference = 'Stop'

$resolvedAllowedRoot = (Resolve-Path -LiteralPath $AllowedRoot).Path
$resolvedDefaultCwd = (Resolve-Path -LiteralPath $DefaultCwd).Path
$bridgeEntryPoint = Join-Path $PSScriptRoot '..\dist\src\index.js'
$bridgeEntryPoint = (Resolve-Path -LiteralPath $bridgeEntryPoint).Path

$env:CODEX_BRIDGE_ALLOWED_ROOTS = ConvertTo-Json -Compress -InputObject @($resolvedAllowedRoot)
$env:CODEX_BRIDGE_DEFAULT_CWD = $resolvedDefaultCwd
Remove-Item Env:CODEX_BRIDGE_CODEX_COMMAND -ErrorAction SilentlyContinue
Remove-Item Env:CODEX_BRIDGE_CODEX_ARGS -ErrorAction SilentlyContinue
if ($CodexCommand) {
    $env:CODEX_BRIDGE_CODEX_COMMAND = $CodexCommand
}
$env:CODEX_BRIDGE_TRANSPORT = 'stdio'

# tunnel-client has already resolved this value before it launches the MCP
# command. Do not pass the control-plane credential on to the Bridge or Codex.
Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue

& $NodeCommand $bridgeEntryPoint
exit $LASTEXITCODE
