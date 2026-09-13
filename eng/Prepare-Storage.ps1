[CmdletBinding()]
param(
    [string] $SourceRepository,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pin = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'StorageDependency.props'))
$version = $pin.SelectSingleNode('/Project/PropertyGroup/StoragePackageVersion').InnerText.Trim()
$revision = $pin.SelectSingleNode('/Project/PropertyGroup/StorageSourceRevision').InnerText.Trim()
$pinRepositoryUrl = $pin.SelectSingleNode('/Project/PropertyGroup/StorageRepositoryUrl').InnerText.Trim()
if ($revision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'StorageSourceRevision must be the final 40-character atelia-storage commit. Candidate feeds may be tested before this pin is finalized; fixed-source preparation may not.'
}
if (!$SourceRepository) { $SourceRepository = $pinRepositoryUrl }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.artifacts/storage-feed' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$packageIds = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.RbfSegmentStore', 'Atelia.EventJournal')
$manifestPath = Join-Path $OutputDirectory "manifest.$version.json"

function Read-PackageIdentity([string] $Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $Path" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        return @{ Id = [string]$metadata.id; Version = [string]$metadata.version }
    }
    finally { $archive.Dispose() }
}

function Read-VerifiedManifest {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $version -or $manifest.sourceRevision -ine $revision) {
        throw "Storage provenance does not match the pin: $manifestPath"
    }
    $manifestRepository = ([string]$manifest.repositoryUrl).TrimEnd('/') -replace '\.git$', ''
    $expectedRepository = $pinRepositoryUrl.TrimEnd('/') -replace '\.git$', ''
    if ($manifestRepository -ine $expectedRepository) { throw "Storage manifest names a different source repository: $manifestPath" }
    if (@($manifest.packages).Count -ne $packageIds.Count) { throw 'Expected exactly five storage packages in the upstream manifest.' }
    foreach ($id in $packageIds) {
        $recorded = @($manifest.packages | Where-Object { $_.id -ceq $id })
        if ($recorded.Count -ne 1 -or $recorded[0].file -cne "$id.$version.nupkg") { throw "Unexpected storage manifest entry for $id." }
        $path = Join-Path $OutputDirectory $recorded[0].file
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing storage package: $path" }
        $identity = Read-PackageIdentity $path
        if ($identity.Id -cne $id -or $identity.Version -cne $version) { throw "Unexpected package identity in $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $recorded[0].sha256) { throw "Storage package bytes changed: $path" }
        if ($recorded[0].symbolsFile -cne "$id.$version.snupkg") { throw "Missing or unexpected symbols manifest entry for $id." }
        $symbols = Join-Path $OutputDirectory $recorded[0].symbolsFile
        if (!(Test-Path -LiteralPath $symbols -PathType Leaf) -or (Get-FileHash -LiteralPath $symbols -Algorithm SHA256).Hash -ine $recorded[0].symbolsSha256) {
            throw "Storage symbols are missing or changed: $symbols"
        }
    }
    return $manifest
}

# Reuse only packages whose recorded fixed revision and bytes still match.
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Read-VerifiedManifest
    Write-Host "Storage packages ready: $version from $revision in $OutputDirectory"
    return [pscustomobject]@{ PackageSource = $OutputDirectory; Version = $version; Revision = $revision; Packages = $manifest.packages }
}

foreach ($id in $packageIds) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory "$id.$version.nupkg")) {
        throw "Refusing to overwrite an existing $id/$version without matching provenance. Use a fresh output directory or a new version."
    }
}

$checkout = Join-Path $repositoryRoot ".artifacts/storage-source/$revision"
if (!(Test-Path -LiteralPath $checkout)) {
    [void](New-Item -ItemType Directory -Force -Path (Split-Path $checkout))
    & git clone --no-local --no-checkout -- $SourceRepository $checkout | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone the pinned storage source repository.' }
    & git -C $checkout checkout --detach $revision | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not check out storage commit $revision." }
}
$actualRevision = (& git -C $checkout rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $actualRevision -ine $revision) { throw "Storage checkout is not at the pinned commit: $checkout" }
$changes = @(& git -C $checkout status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw "Storage preparation requires a clean fixed checkout: $checkout" }
# SourceRepository chooses where Git obtains objects. Package/Source Link identity
# always names the canonical repository, including for a local acquisition clone.
& git -C $checkout remote set-url origin $pinRepositoryUrl | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Could not set the canonical storage repository URL on the preparation clone.' }
$pack = Join-Path $checkout 'eng/Pack.ps1'
if (!(Test-Path -LiteralPath $pack -PathType Leaf)) { throw "The pinned storage commit does not contain eng/Pack.ps1: $revision" }
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$producedManifest = @(& $pack -Version $version -OutputDirectory $OutputDirectory)
if ($producedManifest.Count -ne 1 -or [IO.Path]::GetFullPath([string]$producedManifest[0]) -cne $manifestPath) { throw "Storage Pack must return only its manifest path; received: $producedManifest" }
$manifest = Read-VerifiedManifest
Write-Host "Storage packages prepared: $version from $revision in $OutputDirectory"
[pscustomobject]@{ PackageSource = $OutputDirectory; Version = $version; Revision = $revision; Packages = $manifest.packages }
