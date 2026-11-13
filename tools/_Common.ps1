# Shared helpers. Run one of the other scripts instead.
# The licensed cache implementation is in _Archives.ps1.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:SnowUtf8 = [Text.UTF8Encoding]::new($false, $true)
Add-Type -AssemblyName System.IO.Compression
. "$PSScriptRoot\_ScriptNames.ps1"
. "$PSScriptRoot\_Archives.ps1"
. "$PSScriptRoot\_Bytecode.ps1"

function Get-SnowHash([byte[]]$Data) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Data))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Get-SnowFileHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SnowPaths([string[]]$Paths) {
    if (-not $Paths -or $Paths.Count -eq 0) { throw 'Supply an input path. Use -Help for examples.' }
    return @($Paths | ForEach-Object { (Get-Item -LiteralPath $_ -ErrorAction Stop).FullName })
}

function Assert-SnowNotLinked([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Links are not supported: $($item.FullName)" }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}

function Get-SnowSafePath([string]$Name) {
    $path = $Name.Replace('\', '/')
    if ([string]::IsNullOrEmpty($path) -or $path.StartsWith('/') -or $path.Contains(':')) { throw "Unsafe member path: $Name" }
    foreach ($part in $path.Split('/')) {
        if ($part -in @('', '.', '..') -or $part -match '[<>"|?*\x00-\x1f]' -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Unsafe member path: $Name" }
    }
    if ($path.Split('/')[0] -ieq '_snowpatcher') { throw 'The _snowpatcher folder is reserved for extraction metadata.' }
    return $path
}

function Assert-SnowEntries($Entries) {
    $members = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries) {
        $path = Get-SnowSafePath $entry.Path
        if (-not $members.Add($path)) { throw "Duplicate member path: $path" }
        if ($entry.Directory) {
            if ($files.Contains($path)) { throw "File/folder collision: $path" }
            [void]$folders.Add($path)
        } else {
            if (-not $files.Add($path) -or $folders.Contains($path)) { throw "Duplicate or colliding member: $path" }
        }
        $parent = $path
        while ($parent.Contains('/')) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            if ($files.Contains($parent)) { throw "File/folder collision: $parent" }
            [void]$folders.Add($parent)
        }
    }
}

function Read-SnowBytes([IO.BinaryReader]$Reader, [long]$Count) {
    if ($Count -lt 0 -or $Count -gt 268435456 -or $Count -gt ($Reader.BaseStream.Length - $Reader.BaseStream.Position)) {
        throw 'Invalid or truncated binary member (maximum member size: 256 MiB).'
    }
    return ,$Reader.ReadBytes([int]$Count)
}

function Assert-SnowMarker([IO.BinaryReader]$Reader, [uint32]$Expected, [switch]$Byte) {
    $actual = if ($Byte) { $Reader.ReadByte() } else { $Reader.ReadUInt32() }
    if ($actual -ne $Expected) { throw 'Unsupported container table layout.' }
}

function Get-SnowKind([byte[]]$Data) {
    if ($Data.Length -ge 16 -and [string]::Equals([Text.Encoding]::ASCII.GetString($Data, 0, 16), "1SERcache_block`0", [StringComparison]::Ordinal)) { return 'Cache' }
    if ($Data.Length -ge 4) {
        $magic = [Text.Encoding]::ASCII.GetString($Data, 0, 4)
        if ($magic -ceq 'S3DB') { return 'Bundle' }
        if ($magic -ceq 'SSLB') { return 'Module' }
        if ($Data[0] -eq 80 -and $Data[1] -eq 75) { return 'Zip' }
    }
    return 'Raw'
}

function Get-SnowWorkPath([string]$Name, [string]$OutputPath) {
    if ($OutputPath) { return [IO.Path]::GetFullPath($OutputPath) }
    $documents = [Environment]::GetFolderPath('MyDocuments')
    if (-not $documents) { throw 'Documents folder not found. Supply -OutputPath.' }
    $parent = Join-Path $documents 'SnowPatcher Tools'
    [void][IO.Directory]::CreateDirectory($parent)
    $path = Join-Path $parent $Name
    if (Test-Path -LiteralPath $path) { $path += '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6) }
    return $path
}

function Write-SnowJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), $script:SnowUtf8)
}

function Get-SnowFolderFiles([string]$Folder) {
    Assert-SnowNotLinked $Folder
    $stack = [Collections.Generic.Stack[string]]::new(); $stack.Push($Folder)
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($directory -eq $Folder -and $item.Name -ieq '_snowpatcher') { continue }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Links are not supported: $($item.FullName)" }
            if ($item.PSIsContainer) { $stack.Push($item.FullName) } else { $item }
        }
    }
}

function Compare-SnowData([byte[]]$Before, [byte[]]$After, [string]$Name, [int]$Depth = 0) {
    $beforeHash = Get-SnowHash $Before; $afterHash = Get-SnowHash $After
    if ($beforeHash -eq $afterHash) { return }
    if ($Depth -gt 8) { throw 'Containers are nested too deeply.' }
    $kind = Get-SnowKind $Before
    if ($kind -eq (Get-SnowKind $After) -and $kind -in @('Zip', 'Cache', 'Bundle')) {
        $left = Read-SnowContainer $Before; $right = Read-SnowContainer $After
        $leftMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        $rightMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($entry in $left.Entries) { $leftMap.Add($entry.Name, $entry) }
        foreach ($entry in $right.Entries) { $rightMap.Add($entry.Name, $entry) }
        $names = @(@($leftMap.Keys) + @($rightMap.Keys) | Sort-Object -Unique -CaseSensitive)
        foreach ($memberName in $names) {
            $path = $Name + '!' + $memberName
            if (-not $leftMap.ContainsKey($memberName)) { [pscustomobject]@{ Change='Added'; Path=$path } }
            elseif (-not $rightMap.ContainsKey($memberName)) { [pscustomobject]@{ Change='Removed'; Path=$path } }
            else { Compare-SnowData $leftMap[$memberName].Data $rightMap[$memberName].Data $path ($Depth + 1) }
        }
        # Metadata changes matter even when member contents are equal.
        [pscustomobject]@{ Change='Container changed'; Path=$Name; BeforeSha256=$beforeHash; AfterSha256=$afterHash }
    } elseif ($kind -eq 'Module' -and (Get-SnowKind $After) -eq 'Module') {
        $left = Read-SnowModule $Before $Name; $right = Read-SnowModule $After $Name
        if ($left.ContextSha256 -ne $right.ContextSha256) { [pscustomobject]@{ Change='Module tables changed'; Path=$Name } }
        $leftMap = @{}; $rightMap = @{}
        foreach ($function in $left.Functions) { $leftMap[$function.Class + '::' + $function.Name + '#' + $function.Index] = $function }
        foreach ($function in $right.Functions) { $rightMap[$function.Class + '::' + $function.Name + '#' + $function.Index] = $function }
        foreach ($key in @(@($leftMap.Keys) + @($rightMap.Keys) | Sort-Object -Unique)) {
            $path = $Name + '!' + $key
            if (-not $leftMap.ContainsKey($key)) { [pscustomobject]@{ Change='Function added'; Path=$path } }
            elseif (-not $rightMap.ContainsKey($key)) { [pscustomobject]@{ Change='Function removed'; Path=$path } }
            elseif ($leftMap[$key].Sha256 -ne $rightMap[$key].Sha256) {
                [pscustomobject]@{ Change='Function changed'; Path=$path; BeforeSha256=$leftMap[$key].Sha256; AfterSha256=$rightMap[$key].Sha256 }
            }
        }
        [pscustomobject]@{ Change='Module changed'; Path=$Name; BeforeSha256=$beforeHash; AfterSha256=$afterHash }
    } else { [pscustomobject]@{ Change='Changed'; Path=$Name; BeforeSha256=$beforeHash; AfterSha256=$afterHash } }
}
