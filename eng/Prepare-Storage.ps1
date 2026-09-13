[CmdletBinding()]
param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pin = [xml](Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'StorageDependency.props'))
$version = $pin.SelectSingleNode('/Project/PropertyGroup/StoragePackageVersion').InnerText.Trim()
$revision = $pin.SelectSingleNode('/Project/PropertyGroup/StorageSourceRevision').InnerText.Trim()
$pinRepositoryUrl = $pin.SelectSingleNode('/Project/PropertyGroup/StorageRepositoryUrl').InnerText.Trim()
if ($revision -notmatch '^[0-9a-fA-F]{40}$') { throw 'StorageSourceRevision must be the published package source commit (40 hexadecimal characters).' }
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'StoragePackageVersion must be an explicit normalized NuGet version without build metadata.'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.artifacts/storage-feed' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$packageIds = @('Atelia.Primitives', 'Atelia.Data', 'Atelia.Rbf', 'Atelia.RbfSegmentStore', 'Atelia.EventJournal')
$manifestPath = Join-Path $OutputDirectory "manifest.$version.json"
$expectedRepository = $pinRepositoryUrl.TrimEnd('/') -replace '\.git$', ''

function Assert-PublishedPackage([string] $Path, [string] $Id) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in $Path" }
        # Presence is checked here. This is not full signature trust-chain verification.
        if (!$archive.GetEntry('.signature.p7s')) { throw "Expected a signed nuget.org package: $Path" }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entries[0].Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $nuspec = [Xml.XmlDocument]::new()
                $nuspec.XmlResolver = $null
                $nuspec.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if (!$metadata) { throw "Missing package metadata: $Path" }
        $actualId = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
        $actualVersion = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        if ($actualId -cne $Id -or $actualVersion -cne $version) { throw "Unexpected package identity: $Path" }
        if (!$repository -or $repository.GetAttribute('commit') -ine $revision) { throw "Package source commit differs from the pin: $Path" }
        $actualRepository = $repository.GetAttribute('url').TrimEnd('/') -replace '\.git$', ''
        if ($actualRepository -ine $expectedRepository) { throw "Package source repository differs from the pin: $Path" }
    }
    finally { $archive.Dispose() }
}

function Read-VerifiedManifest {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 2 -or $manifest.source -cne 'nuget.org' -or $manifest.version -cne $version -or $manifest.sourceRevision -ine $revision) {
        throw "Expected published nuget.org provenance matching the pin: $manifestPath. Local schema 1 feeds cannot be reused as published packages."
    }
    $manifestRepository = ([string]$manifest.repositoryUrl).TrimEnd('/') -replace '\.git$', ''
    if ($manifestRepository -ine $expectedRepository) { throw "Storage manifest names a different repository: $manifestPath" }
    if (@($manifest.packages).Count -ne $packageIds.Count) { throw 'Expected exactly five published storage packages in the manifest.' }
    foreach ($id in $packageIds) {
        $recorded = @($manifest.packages | Where-Object { $_.id -ceq $id })
        if ($recorded.Count -ne 1 -or $recorded[0].file -cne "$id.$version.nupkg") { throw "Unexpected storage manifest entry for $id." }
        $path = Join-Path $OutputDirectory $recorded[0].file
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing storage package: $path" }
        Assert-PublishedPackage $path $id
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $recorded[0].sha256) { throw "Storage package bytes changed: $path" }
    }
    return $manifest
}

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Read-VerifiedManifest
    Write-Host "Published storage packages ready: $version from $revision in $OutputDirectory"
    return [pscustomobject]@{ PackageSource = $OutputDirectory; Version = $version; Revision = $revision; Packages = $manifest.packages }
}

foreach ($id in $packageIds) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory "$id.$version.nupkg")) {
        throw "Refusing to overwrite existing $id/$version without published provenance. Use a fresh output directory."
    }
}

$temporaryRoot = Join-Path $repositoryRoot '.artifacts'
[void][IO.Directory]::CreateDirectory($temporaryRoot)
$downloadDirectory = Join-Path $temporaryRoot ("storage-download-" + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($downloadDirectory)
try {
    $packages = foreach ($id in $packageIds) {
        $file = "$id.$version.nupkg"
        $path = Join-Path $downloadDirectory $file
        $lowerId = $id.ToLowerInvariant()
        $lowerVersion = $version.ToLowerInvariant()
        $url = "https://api.nuget.org/v3-flatcontainer/$lowerId/$lowerVersion/$lowerId.$lowerVersion.nupkg"
        Write-Host "Downloading $id/$version from nuget.org"
        Invoke-WebRequest -Uri $url -OutFile $path
        Assert-PublishedPackage $path $id
        [pscustomobject]@{ id = $id; file = $file; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [ordered]@{
        schemaVersion = 2
        source = 'nuget.org'
        version = $version
        sourceRevision = $revision
        repositoryUrl = $pinRepositoryUrl
        packages = @($packages)
    }
    $temporaryManifest = Join-Path $downloadDirectory "manifest.$version.json"
    [IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    # Only publish the feed after all five downloads and checks succeed. Move never
    # overwrites an existing file; the manifest is the final completion marker.
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    foreach ($package in $packages) {
        [IO.File]::Move((Join-Path $downloadDirectory $package.file), (Join-Path $OutputDirectory $package.file))
    }
    [IO.File]::Move($temporaryManifest, $manifestPath)
}
finally {
    # Only this invocation's flat staging directory is owned by this script.
    foreach ($temporaryFile in [IO.Directory]::GetFiles($downloadDirectory)) { [IO.File]::Delete($temporaryFile) }
    [IO.Directory]::Delete($downloadDirectory, $false)
}
$manifest = Read-VerifiedManifest
Write-Host "Published storage packages prepared: $version from $revision in $OutputDirectory"
[pscustomobject]@{ PackageSource = $OutputDirectory; Version = $version; Revision = $revision; Packages = $manifest.packages }
