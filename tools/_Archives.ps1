# Shared container helpers. Loaded by _Common.ps1.
<#
The cache_block reader/writer is adapted from chase-000/SnowRunnerTools.

MIT License

Copyright (c) 2020 chase <s.chubukov@protonmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
#>

function Read-SnowContainer([byte[]]$Data) {
    $kind = Get-SnowKind $Data
    if ($kind -notin @('Cache', 'Bundle', 'Zip')) { throw 'Expected a .pak/.zip, cache_block or SSL bundle.' }
    $entries = [Collections.Generic.List[object]]::new()
    $memory = [IO.MemoryStream]::new($Data, $false)
    $reader = [IO.BinaryReader]::new($memory)
    $header = [byte[]]@()
    try {
        if ($kind -eq 'Zip') {
            $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read, $true)
            try {
                if ($zip.Entries.Count -gt 100000) { throw 'Too many ZIP members.' }
                [long]$total = 0
                foreach ($member in $zip.Entries) {
                    $directory = $member.FullName.EndsWith('/') -or $member.FullName.EndsWith('\')
                    $path = Get-SnowSafePath ($member.FullName.TrimEnd([char[]]@('/', '\')))
                    $attributes = [long]$member.ExternalAttributes
                    if (($attributes -band 0xf0000000L) -eq 0xa0000000L -or ($attributes -band 0x400) -ne 0) { throw "ZIP link rejected: $path" }
                    $total += $member.Length
                    if ($member.Length -gt 268435456 -or $total -gt 1073741824) { throw 'ZIP exceeds the extraction size limits.' }
                    $body = [byte[]]@()
                    if (-not $directory) {
                        $memberStream = $member.Open(); $output = [IO.MemoryStream]::new()
                        try {
                            $buffer = [byte[]]::new(65536)
                            while (($read = $memberStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                                if ($output.Length + $read -gt $member.Length) { throw "ZIP member exceeds its declared size: $path" }
                                $output.Write($buffer, 0, $read)
                            }
                            $body = $output.ToArray()
                        } finally { $memberStream.Dispose(); $output.Dispose() }
                        if ($body.Length -ne $member.Length) { throw "Truncated ZIP member: $path" }
                    }
                    $entries.Add([pscustomobject]@{ Name=$member.FullName; Path=$path; Data=$body; Directory=$directory;
                        Attributes=$member.ExternalAttributes; Timestamp=$member.LastWriteTime.ToString('o') })
                }
            } finally { $zip.Dispose() }
        } else {
            $headerSize = if ($kind -eq 'Cache') { 64 } else { 20 }
            $header = Read-SnowBytes $reader $headerSize
            if ($kind -eq 'Cache') {
                Assert-SnowMarker $reader 1; Assert-SnowMarker $reader 1 -Byte
                $count = $reader.ReadUInt32()
                Assert-SnowMarker $reader 4; Assert-SnowMarker $reader 1 -Byte
            } else {
                $count = [BitConverter]::ToUInt32($header, 11)
            }
            if ($count -gt 100000) { throw 'Too many container members.' }
            $names = [Collections.Generic.List[string]]::new()
            for ($i = 0; $i -lt $count; $i++) {
                $length = $reader.ReadUInt32()
                if ($length -gt 8192) { throw 'Invalid member name length.' }
                $names.Add($script:SnowUtf8.GetString((Read-SnowBytes $reader $length)))
            }
            $offsets = [Collections.Generic.List[uint64]]::new()
            if ($kind -eq 'Cache') {
                Assert-SnowMarker $reader 1 -Byte
                for ($i = 0; $i -lt $count; $i++) { $offsets.Add($reader.ReadUInt64()) }
            }
            Assert-SnowMarker $reader 1 -Byte
            $sizes = [Collections.Generic.List[uint32]]::new()
            for ($i = 0; $i -lt $count; $i++) { $sizes.Add($reader.ReadUInt32()) }
            if ($kind -eq 'Cache') {
                Assert-SnowMarker $reader 1 -Byte
                for ($i = 0; $i -lt $count; $i++) { Assert-SnowMarker $reader 0 }
            }
            [long]$offset = 0
            $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            for ($i = 0; $i -lt $count; $i++) {
                $name = $names[$i]
                if (-not $unique.Add($name)) { throw "Duplicate container member: $name" }
                if ($kind -eq 'Cache') {
                    if ($offsets[$i] -ne $offset) { throw 'Unsupported cache member offsets.' }
                    if ($name -notmatch '^<([^>]+)>(:|\\.+\\)([^\\]+)$') { throw "Unsupported cache member name: $name" }
                    $namespace = $Matches[1]; $directory = $Matches[2]; $leaf = $Matches[3]
                    $middle = if ($directory -eq ':') { '' } else { $directory.Trim('\').Replace('\', '/') + '/' }
                    $path = Get-SnowSafePath "[$namespace]/$middle$leaf"
                } else {
                    # Script names are identifiers, not filesystem paths. Keep their full names in the manifest.
                    $leaf = ($name.Split('.')[-1] -replace '[^a-zA-Z0-9_-]', '_')
                    if ($leaf.Length -gt 60) { $leaf = $leaf.Substring(0, 60) }
                    $path = 'modules/{0:D5}-{1}.sslb' -f $i, $leaf
                }
                $body = Read-SnowBytes $reader $sizes[$i]
                $entries.Add([pscustomobject]@{ Name=$name; Path=$path; Data=$body; Directory=$false; Attributes=0; Timestamp=$null })
                $offset += $sizes[$i]
            }
            if ($memory.Position -ne $memory.Length) { throw 'Unexpected container trailing data.' }
        }
    } finally { $reader.Dispose(); $memory.Dispose() }
    Assert-SnowEntries $entries
    return [pscustomobject]@{ Kind=$kind; Header=$header; Entries=$entries.ToArray() }
}

function Expand-SnowContainer([string]$Path, [string]$OutputPath) {
    Assert-SnowNotLinked $Path
    $data = [IO.File]::ReadAllBytes($Path)
    $container = Read-SnowContainer $data
    $target = Get-SnowWorkPath (([IO.Path]::GetFileName($Path)) + '-extracted') $OutputPath
    if (Test-Path -LiteralPath $target) { throw "Output already exists: $target" }
    [void][IO.Directory]::CreateDirectory($target)
    Assert-SnowNotLinked $target
    $records = [Collections.Generic.List[object]]::new()
    foreach ($entry in $container.Entries) {
        $destination = Join-Path $target $entry.Path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ($entry.Directory) { [void][IO.Directory]::CreateDirectory($destination) }
        else {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::WriteAllBytes($destination, $entry.Data)
        }
        $records.Add([pscustomobject]@{ Name=$entry.Name; Path=$entry.Path; Directory=$entry.Directory;
            Sha256=(Get-SnowHash $entry.Data); Attributes=$entry.Attributes; Timestamp=$entry.Timestamp })
    }
    $metadata = Join-Path $target '_snowpatcher'
    [void][IO.Directory]::CreateDirectory($metadata)
    [IO.File]::WriteAllBytes((Join-Path $metadata 'original.bin'), $data)
    Write-SnowJson (Join-Path $metadata 'manifest.json') ([ordered]@{
        Format=1; Kind=$container.Kind; Name=[IO.Path]::GetFileName($Path); SourceSha256=(Get-SnowHash $data)
        Header=[Convert]::ToBase64String($container.Header); Entries=$records.ToArray()
    })
    return $target
}

function Write-SnowContainer([string]$Path, [string]$Kind, [byte[]]$Header, $Entries) {
    Assert-SnowEntries $Entries
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        if ($Kind -eq 'Zip') {
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($entry in $Entries) {
                    $member = $zip.CreateEntry($entry.Name, [IO.Compression.CompressionLevel]::Optimal)
                    $member.ExternalAttributes = [int]$entry.Attributes
                    if ($entry.Timestamp) { $member.LastWriteTime = [DateTimeOffset]::Parse($entry.Timestamp, [Globalization.CultureInfo]::InvariantCulture) }
                    if (-not $entry.Directory) {
                        $output = $member.Open()
                        try { $output.Write($entry.Data, 0, $entry.Data.Length) } finally { $output.Dispose() }
                    }
                }
            } finally { $zip.Dispose() }
        } else {
            $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
            try {
                if ($Kind -eq 'Cache') {
                    if ($Header.Length -ne 64 -or (Get-SnowKind $Header) -ne 'Cache') { throw 'Invalid cache header.' }
                    $writer.Write($Header); $writer.Write([uint32]1); $writer.Write([byte]1); $writer.Write([uint32]$Entries.Count)
                    $writer.Write([uint32]4); $writer.Write([byte]1)
                } elseif ($Kind -eq 'Bundle') {
                    if ($Header.Length -ne 20 -or (Get-SnowKind $Header) -ne 'Bundle') { throw 'Invalid bundle header.' }
                    [Array]::Copy([BitConverter]::GetBytes([uint32]$Entries.Count), 0, $Header, 11, 4)
                    $writer.Write($Header)
                } else { throw 'Unsupported output format.' }
                foreach ($entry in $Entries) {
                    $name = $script:SnowUtf8.GetBytes($entry.Name)
                    $writer.Write([uint32]$name.Length); $writer.Write($name)
                }
                if ($Kind -eq 'Cache') {
                    $writer.Write([byte]1); [uint64]$offset = 0
                    foreach ($entry in $Entries) { $writer.Write($offset); $offset += $entry.Data.Length }
                }
                $writer.Write([byte]1)
                foreach ($entry in $Entries) { $writer.Write([uint32]$entry.Data.Length) }
                if ($Kind -eq 'Cache') {
                    $writer.Write([byte]1)
                    foreach ($entry in $Entries) { $writer.Write([uint32]0) }
                }
                foreach ($entry in $Entries) { $writer.Write([byte[]]$entry.Data) }
            } finally { $writer.Dispose() }
        }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

function Compress-SnowContainer([string]$Folder, [string]$OutputPath) {
    Assert-SnowNotLinked $Folder
    $folderPath = [IO.Path]::GetFullPath($Folder).TrimEnd('\', '/')
    $metadataPath = Join-Path $folderPath '_snowpatcher/manifest.json'
    Assert-SnowNotLinked $metadataPath
    $metadata = [IO.File]::ReadAllText($metadataPath) | ConvertFrom-Json
    if ($metadata.Format -ne 1 -or $metadata.Kind -notin @('Zip', 'Cache', 'Bundle')) { throw 'Unsupported extraction manifest.' }
    $source = Join-Path $folderPath '_snowpatcher/original.bin'
    Assert-SnowNotLinked $source
    if ((Get-SnowFileHash $source) -ne $metadata.SourceSha256) { throw 'The original extraction copy has changed. Extract again.' }
    $original = Read-SnowContainer ([IO.File]::ReadAllBytes($source))
    $entries = [Collections.Generic.List[object]]::new()
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $workingFiles = @(Get-SnowFolderFiles $folderPath)
    foreach ($entry in $metadata.Entries) {
        $safe = Get-SnowSafePath $entry.Path
        if (-not $listed.Add($safe)) { throw "Duplicate manifest path: $safe" }
        $path = Join-Path $folderPath $safe.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([bool]$entry.Directory -ne [IO.Directory]::Exists($path)) { throw "Missing member or wrong file type: $safe" }
        $body = [byte[]]@()
        if (-not $entry.Directory) { $body = [IO.File]::ReadAllBytes($path) }
        $entries.Add([pscustomobject]@{ Name=[string]$entry.Name; Path=$safe; Data=$body; Directory=[bool]$entry.Directory;
            Attributes=$entry.Attributes; Timestamp=$entry.Timestamp })
    }
    # Unlisted files are probably a mistake. Add deliberate members to the manifest explicitly.
    foreach ($file in $workingFiles) {
        $relative = $file.FullName.Substring($folderPath.Length + 1).Replace('\', '/')
        if ($relative.StartsWith('_snowpatcher/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $listed.Contains($relative)) { throw "Unlisted file: $relative. Add a manifest entry, or move it outside the extraction folder." }
    }
    Assert-SnowEntries $entries
    $name = [IO.Path]::GetFileNameWithoutExtension($metadata.Name) + '-repacked' + [IO.Path]::GetExtension($metadata.Name)
    $target = Get-SnowWorkPath $name $OutputPath
    if (Test-Path -LiteralPath $target) { throw "Output already exists: $target" }
    $parent = [IO.Path]::GetDirectoryName($target)
    [void][IO.Directory]::CreateDirectory($parent)
    Assert-SnowNotLinked $parent
    $temporary = Join-Path $parent ('.snowpatcher-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $unchanged = $original.Kind -eq $metadata.Kind -and $original.Entries.Count -eq $entries.Count -and
            [Convert]::ToBase64String($original.Header) -ceq $metadata.Header
        for ($i = 0; $unchanged -and $i -lt $entries.Count; $i++) {
            $left = $original.Entries[$i]; $right = $entries[$i]
            $unchanged = $left.Name -ceq $right.Name -and $left.Directory -eq $right.Directory -and
                $left.Attributes -eq $right.Attributes -and $left.Timestamp -ceq $right.Timestamp -and
                (Get-SnowHash $left.Data) -eq (Get-SnowHash $right.Data)
        }
        if ($unchanged) { [IO.File]::Copy($source, $temporary, $false) }
        else { Write-SnowContainer $temporary $metadata.Kind ([Convert]::FromBase64String($metadata.Header)) $entries }
        $check = Read-SnowContainer ([IO.File]::ReadAllBytes($temporary))
        if ($check.Kind -ne $metadata.Kind -or $check.Entries.Count -ne $entries.Count) { throw 'Repacked container failed verification.' }
        for ($i = 0; $i -lt $entries.Count; $i++) {
            if ($check.Entries[$i].Name -cne $entries[$i].Name -or
                (Get-SnowHash $check.Entries[$i].Data) -ne (Get-SnowHash $entries[$i].Data)) { throw 'Repacked member failed verification.' }
        }
        [IO.File]::Move($temporary, $target)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    return $target
}
