[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TunnelClientPath,

    [string] $Profile = 'local-codex-drama-board'
)

$ErrorActionPreference = 'Stop'
$resolvedTunnelClient = (Resolve-Path -LiteralPath $TunnelClientPath).Path
$runtimeKey = Read-Host 'CONTROL_PLANE_API_KEY (input is hidden)' -AsSecureString
$runtimeKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($runtimeKey)

try {
    $env:CONTROL_PLANE_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($runtimeKeyPointer)

    & $resolvedTunnelClient doctor --profile $Profile --explain
    if ($LASTEXITCODE -ne 0) {
        throw "tunnel-client doctor failed with exit code $LASTEXITCODE."
    }

    Write-Host "Starting tunnel profile '$Profile'. Press Ctrl+C to stop it."
    & $resolvedTunnelClient run --profile $Profile
    if ($LASTEXITCODE -ne 0) {
        throw "tunnel-client run failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:CONTROL_PLANE_API_KEY -ErrorAction SilentlyContinue
    if ($runtimeKeyPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($runtimeKeyPointer)
    }
}
