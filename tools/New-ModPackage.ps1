<#
.SYNOPSIS
Build a SnowPatcher ZIP from readable authoring files and original game files.
.DESCRIPTION
Reads game files without changing them. Resolves instruction names and references,
generates guards and payload hashes, and expands declared bundle variants.
The first build records snowpatcher.lock.json beside the source. Later builds
refuse changed baseline resources unless -RefreshBaseline is explicitly supplied.
Review changed game code before refreshing that lock.
.EXAMPLE
.\New-ModPackage.ps1 '.\mods\snowpatcher-indicator' -GameRoot 'D:\OriginalSnowRunner' -OutputPath 'D:\ModWork\Indicator.zip'
.EXAMPLE
.\New-ModPackage.ps1 '.\examples\authoring' -GameRoot 'D:\OriginalSnowRunner'
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0)][string]$InputPath,
    [string]$GameRoot,
    [string]$OutputPath,
    [switch]$RefreshBaseline,
    [switch]$Strict,
    [switch]$Help
)
. "$PSScriptRoot\_Common.ps1"
. "$PSScriptRoot\_Authoring.ps1"
. "$PSScriptRoot\_Portable.ps1"
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
$temporary = $null
$lockTemporary = $null
$published = $false
try {
    if (-not $InputPath -or -not $GameRoot) { throw 'Supply the source folder and -GameRoot pointing to known original game files. Use -Help for examples.' }
    $inputItem = Get-Item -LiteralPath $InputPath
    $sourcePath = if ($inputItem.PSIsContainer) { Join-Path $inputItem.FullName 'snowpatcher.source.json' } else { $inputItem.FullName }
    Assert-SnowNotLinked $sourcePath
    $script:SnowAuthorRoot = [IO.Path]::GetDirectoryName($sourcePath)
    $script:SnowAuthorGameRoot = (Get-Item -LiteralPath $GameRoot).FullName.TrimEnd('\','/')
    Assert-SnowNotLinked $script:SnowAuthorGameRoot
    if (-not [IO.Directory]::Exists($script:SnowAuthorGameRoot)) { throw '-GameRoot must be a folder.' }
    if ($script:SnowAuthorRoot -eq $script:SnowAuthorGameRoot -or $script:SnowAuthorRoot.StartsWith($script:SnowAuthorGameRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep authoring files outside the game folder.' }
    $source = Read-SnowAuthorJson ([IO.File]::ReadAllText($sourcePath))
    $script:SnowAuthorSource = $source
    foreach ($key in $source.Keys) {
        if ($key -notin @('authoringVersion','id','name','version','minSnowPatcherVersion','authors','description','modioUrl','testedBuilds','dependencies','conflicts','variants','targets','checks','include','operations','propertyChecks','functionChecks')) { throw "Unknown authoring field '$key'." }
    }
    if (-not $source.Contains('authoringVersion') -or $source.authoringVersion -ne 1) { throw 'Expected authoringVersion 1.' }
    foreach ($key in @('id','name','version','authors','operations')) { if (-not $source.Contains($key) -or -not $source[$key]) { throw "Missing source field '$key'." } }
    if ($source.id -cnotmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$' -or $source.version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Use a lowercase mod ID and a MAJOR.MINOR.PATCH version.' }
    $minimumVersion = if ($source.Contains('minSnowPatcherVersion')) { [string]$source.minSnowPatcherVersion } else { '1.1.0' }
    if ($minimumVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or $minimumVersion -cmatch '^(0\.|1\.0\.)') { throw 'Package format 3 needs minSnowPatcherVersion 1.1.0 or newer.' }
    if ($source.Contains('modioUrl') -and ($source.modioUrl -isnot [string] -or $source.modioUrl.Length -gt 2048 -or $source.modioUrl -cnotmatch '\Ahttps://(?:www\.)?mod\.io/g/snowrunner/m/[A-Za-z0-9][A-Za-z0-9_-]*/?\z')) { throw 'modioUrl must be an HTTPS SnowRunner mod page on mod.io.' }
    if ($source.Contains('variants') -and @($source.variants | Sort-Object -Unique).Count -ne $source.variants.Count) { throw 'Duplicate bundle variant.' }
    $script:SnowAuthorBytes = @{}
    $script:SnowAuthorContainers = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $script:SnowAuthorReports = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $script:SnowAuthorObserved = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $script:SnowAuthorChecks = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $script:SnowExplicitChecks=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($source.Contains('checks')) {
        foreach ($check in $source.checks) {
            foreach ($variant in @(Get-SnowAuthorVariants $check $source)) { $explicit=Get-SnowAuthorTarget $check $source $variant; [void]$script:SnowExplicitChecks.Add((ConvertTo-Json -InputObject $explicit -Depth 16 -Compress)); Add-SnowAuthorCheck $explicit }
        }
    }
    $portable = -not $Strict -and @($source.operations | Where-Object { $_.type -in @('sslb.function.edit','sslb.methods.add') }).Count -gt 0
    if(-not $portable){
        foreach($key in @('propertyChecks','functionChecks')){
            if($source.Contains($key)){foreach($check in $source[$key]){foreach($variant in @(Get-SnowAuthorVariants $check.target $source)){Add-SnowAuthorCheck (Get-SnowAuthorTarget $check.target $source $variant)}}}
        }
    }
    $script:SnowPortableFunctions=[Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $script:SnowPortableNative=[Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
    $script:SnowPortableNativeRecords=@{}
    $script:SnowPortableOperands=[Collections.Generic.List[object]]::new()
    if($portable){$script:SnowPortableNativeText=$script:SnowUtf8.GetString((Get-SnowAuthorResource @{file='Sources/BinEditor/meta_desc_ssl.ps';members=@()}))}
    $operations = [Collections.Generic.List[object]]::new()
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($definition in $source.operations) {
        foreach ($key in @('id','type','target')) { if (-not $definition.Contains($key)) { throw "An operation is missing '$key'." } }
        foreach ($variant in @(Get-SnowAuthorVariants $definition.target $source)) {
            $script:SnowAuthorVariant = $variant
            $operation = ConvertTo-SnowAuthorOperation $definition $source $variant
            if ($operation.id.Length -gt 128 -or $operation.id -cnotmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') { throw "Invalid operation ID '$($operation.id)'. Use up to 128 lowercase letters, numbers, dots and hyphens." }
            if (-not $ids.Add($operation.id)) { throw "Duplicate operation ID '$($operation.id)'." }
            if($portable){Add-SnowPortableOperation $definition $operation}
            $operations.Add($operation)
        }
    }
    if (@($operations | Where-Object type -EQ 'xml.edit').Count -gt 0 -and [version]$minimumVersion -lt [version]'1.3.0') {
        throw 'XML edits need minSnowPatcherVersion 1.3.0 or newer.'
    }
    $payloadPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($operation in $operations) {
        if ($operation.Contains('payload')) { [void]$payloadPaths.Add((Get-SnowSafePath $operation.payload)) }
        if ($operation.Contains('edits')) {
            foreach ($edit in $operation.edits) {
                if ($edit.Contains('value') -and $edit.value -is [Collections.IDictionary] -and $edit.value.Contains('ssoFile')) { [void]$payloadPaths.Add((Get-SnowSafePath $edit.value.ssoFile)) }
                if ($edit.Contains('before') -and $edit.before.Contains('value') -and $edit.before.value -is [Collections.IDictionary] -and $edit.before.value.Contains('ssoFile')) { [void]$payloadPaths.Add((Get-SnowSafePath $edit.before.value.ssoFile)) }
            }
        }
    }
    if ($source.Contains('include')) { foreach ($path in $source.include) { [void]$payloadPaths.Add((Get-SnowSafePath $path)) } }
    $hasCompiledPayload = $false
    $payloads = [ordered]@{}; $files = [Collections.Generic.List[object]]::new()
    foreach ($path in @($payloadPaths | Sort-Object -CaseSensitive)) {
        if ($path -ieq 'snowpatcher.json') { throw 'snowpatcher.json is reserved for the generated manifest.' }
        $file = Get-SnowAuthorFile $script:SnowAuthorRoot $path
        if ((Get-Item -LiteralPath $file).Length -gt 268435456) { throw 'Payload exceeds the 256 MiB limit.' }
        $bytes = [IO.File]::ReadAllBytes($file)
        $payloads[$path] = $bytes
        $files.Add(@{ path=$path; sha256=(Get-SnowHash $bytes); size=$bytes.Length })
        if (Test-SnowCompiledPayload $bytes) { $hasCompiledPayload=$true; Add-SnowAuthorCheck @{ file='Sources/BinEditor/meta_desc_ssl.ps'; members=@() } }
    }
    $manifest = [ordered]@{ formatVersion=3; minSnowPatcherVersion=$minimumVersion }
    foreach ($key in @('id','name','version','authors','description','modioUrl','testedBuilds','dependencies','conflicts')) {
        if ($source.Contains($key)) { $manifest[$key] = $source[$key] }
    }
    $manifest.game = 'snowrunner'
    $manifest.checks = @($script:SnowAuthorChecks.Values | Where-Object {
        -not $portable -or $script:SnowExplicitChecks.Contains((ConvertTo-Json -InputObject $_.target -Depth 16 -Compress)) -or ($_.target.file -eq 'Sources/BinEditor/meta_desc_ssl.ps' -and $hasCompiledPayload) -or
        ($_.target.file -ne 'Sources/BinEditor/meta_desc_ssl.ps' -and -not @($_.target.members | Where-Object format -EQ 'sslbundle').Count)
    })
    if($portable){
        foreach($check in $(if($source.Contains('functionChecks')){$source.functionChecks}else{@()})){
            foreach($variant in @(Get-SnowAuthorVariants $check.target $source)){
                Add-SnowPortableFunction (Get-SnowAuthorTarget $check.target $source $variant) $check.class $check.name
            }
        }
        foreach($check in $(if($source.Contains('propertyChecks')){$source.propertyChecks}else{@()})){
            $target=Get-SnowAuthorTarget $check.target $source ''
            $fragment=Get-SnowPortableFragment ($script:SnowUtf8.GetString((Get-SnowAuthorResource $target))) $check.path
            if($null -eq $fragment){throw 'A required property check was not found.'}
            $script:SnowPortableNative['property:'+(ConvertTo-Json -InputObject $target -Depth 16 -Compress)+':'+($check.path -join '/')]=@{target=$target;path=$check.path;sha256=(Get-SnowPortableTokenHash $fragment)}
        }
        $manifest.relocations=@{operands=$script:SnowPortableOperands.ToArray();functions=@($script:SnowPortableFunctions.Values);values=@($script:SnowPortableNative.Values)}
    }
    $manifest.files = $files.ToArray()
    $manifest.operations = $operations.ToArray()
    $lockPath = Join-Path $script:SnowAuthorRoot 'snowpatcher.lock.json'
    if ([IO.File]::Exists($lockPath)) {
        Assert-SnowNotLinked $lockPath
        $previous = Read-SnowAuthorJson ([IO.File]::ReadAllText($lockPath))
        if ($previous.version -ne 1) { throw 'Unsupported authoring lock version.' }
        if (-not $RefreshBaseline) {
            foreach ($key in $script:SnowAuthorObserved.Keys) {
                $oldHash = if ($previous.resources.Contains($key)) { $previous.resources[$key] } else { $null }
                # Accept the object-shaped locks made by the first development build.
                if ($oldHash -is [Collections.IDictionary]) { $oldHash = $oldHash.sha256 }
                if ($null -ne $oldHash -and $oldHash -cne $script:SnowAuthorObserved[$key]) {
                    throw "Baseline changed: $key. Review the game changes, then use -RefreshBaseline to accept a new authoring baseline."
                }
            }
        }
    }
    # Refuse a moving installation, including an update that completes mid-build.
    foreach ($key in $script:SnowAuthorBytes.Keys) {
        $path = Get-SnowAuthorFile $script:SnowAuthorGameRoot $key
        if ((Get-SnowFileHash $path) -cne (Get-SnowHash $script:SnowAuthorBytes[$key])) { throw 'Game files changed during packaging. Build again from a stable original copy.' }
    }
    $destination = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else {
        Join-Path (Get-SnowWorkPath 'packages' '') ($source.id + '-' + $source.version + '.zip')
    }
    if ($destination.StartsWith($script:SnowAuthorGameRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output cannot be inside the game folder.' }
    if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
    $parent = [IO.Path]::GetDirectoryName($destination)
    [void][IO.Directory]::CreateDirectory($parent); Assert-SnowNotLinked $parent
    $temporary = Join-Path $parent ('.snowpatcher-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $canonical = ConvertTo-SnowCanonicalObject $manifest
            $entries = [ordered]@{ 'snowpatcher.json'=$script:SnowUtf8.GetBytes((ConvertTo-Json -InputObject $canonical -Depth 64)) }
            foreach ($key in $payloads.Keys) { $entries[$key] = $payloads[$key] }
            foreach ($key in $entries.Keys) {
                $entry = $zip.CreateEntry($key, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
                $output = $entry.Open()
                try { $bytes = $entries[$key]; $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    $lock = [ordered]@{ version=1; resources=$script:SnowAuthorObserved }
    # The lock contains names and hashes only. No game bytes are distributed.
    $lockTemporary = Join-Path $script:SnowAuthorRoot ('.snowpatcher-lock-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($lockTemporary, (ConvertTo-Json -InputObject $lock -Depth 16), $script:SnowUtf8)
    [IO.File]::Move($temporary, $destination); $temporary = $null; $published = $true
    if ([IO.File]::Exists($lockPath)) { [IO.File]::Replace($lockTemporary, $lockPath, [NullString]::Value) }
    else { [IO.File]::Move($lockTemporary, $lockPath) }
    $lockTemporary = $null
    $guardCount=$manifest.checks.Count
    foreach ($operation in $manifest.operations) {
        if ($operation.type -in @('sso.edit','strings.edit','xml.edit')) { $guardCount += $operation.edits.Count }
    }
    if($portable){$guardCount+=$script:SnowPortableFunctions.Count+$script:SnowPortableNative.Count+$script:SnowPortableOperands.Count}
    Write-Host ('Built {0} {1}: {2} operations, {3} compatibility checks.' -f $source.name, $source.version, $operations.Count, $guardCount)
    Write-Output $destination
} catch {
    Write-Verbose $_.ScriptStackTrace
    if ($published -and $lockTemporary -and [IO.File]::Exists($destination)) { [IO.File]::Delete($destination) }
    Write-Error $_.Exception.Message -ErrorAction Continue; exit 1
} finally {
    if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    if ($lockTemporary -and [IO.File]::Exists($lockTemporary)) { [IO.File]::Delete($lockTemporary) }
}
