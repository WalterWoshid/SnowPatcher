# Shared authoring and literal JSON helpers.

function ConvertTo-SnowCanonicalObject($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary]) {
        $result = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        $keys = [string[]]@($Value.Keys)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) { $result[$key] = ConvertTo-SnowCanonicalObject $Value[$key] }
        return ,$result
    }
    if ($Value -is [pscustomobject]) {
        $result = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        $keys = [string[]]@($Value.PSObject.Properties.Name)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) { $result[$key] = ConvertTo-SnowCanonicalObject $Value.$key }
        return ,$result
    }
    if ($Value -is [array] -or $Value -is [Collections.IList]) {
        return ,@(foreach ($item in $Value) { ConvertTo-SnowCanonicalObject $item })
    }
    return $Value
}

function ConvertTo-SnowMap($Value, $StringTokens = $null, [switch]$AsObject) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) {
        if ($null -ne $StringTokens -and $StringTokens.ContainsKey($Value)) { return $StringTokens[$Value] }
        return [string]$Value
    }
    # Windows PowerShell can decorate a root array as a PSCustomObject.
    # Handle its actual array value before enumerating object properties.
    if ($Value -is [array]) { return ,@(foreach ($item in $Value) { ConvertTo-SnowMap $item $StringTokens -AsObject:$AsObject }) }
    if ($Value -is [pscustomobject]) {
        $map = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        foreach ($property in $Value.PSObject.Properties) {
            $name = if ($null -ne $StringTokens -and $StringTokens.ContainsKey($property.Name)) { $StringTokens[$property.Name] } else { $property.Name }
            $map[$name] = ConvertTo-SnowMap $property.Value $StringTokens -AsObject:$AsObject
        }
        if ($AsObject) { return [pscustomobject]$map }
        return ,$map
    }
    if ($Value -is [Collections.IDictionary]) {
        $map = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
        foreach ($key in $Value.Keys) { $map[$key] = ConvertTo-SnowMap $Value[$key] $StringTokens -AsObject:$AsObject }
        if ($AsObject) { return [pscustomobject]$map }
        return ,$map
    }
    if ($Value -is [string] -and $null -ne $StringTokens -and $StringTokens.ContainsKey($Value)) { return $StringTokens[$Value] }
    return $Value
}

function Read-SnowAuthorJson([string]$Text, [switch]$AsObject) {
    # Protect string tokens before using the platform parser. PS 5.1 consumes
    # __type as metadata; PS 7 coerces dates. Both must remain literal JSON data.
    $prefix = 'snowpatcher_string_' + [Guid]::NewGuid().ToString('N') + '_'
    $strings = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $escapes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in (@{ '"'='"'; '\'='\'; '/'='/'; b="`b"; f="`f"; n="`n"; r="`r"; t="`t" }).GetEnumerator()) { $escapes.Add($entry.Key,$entry.Value) }
    $output = [Text.StringBuilder]::new()
    $frames = [Collections.Generic.Stack[object]]::new()
    for ($i=0; $i -lt $Text.Length; $i++) {
        $character = $Text[$i]
        if ($character -eq '"') {
            $value = [Text.StringBuilder]::new(); $i++
            while ($i -lt $Text.Length -and $Text[$i] -ne '"') {
                $part = $Text[$i]
                if ($part -eq '\') {
                    $i++
                    if ($i -ge $Text.Length) { throw 'Unterminated JSON escape.' }
                    $escape = [string]$Text[$i]
                    if ($escape -ceq 'u') {
                        if ($i+4 -ge $Text.Length) { throw 'Truncated Unicode escape.' }
                        $hex = $Text.Substring($i+1,4)
                        if ($hex -cnotmatch '^[0-9A-Fa-f]{4}$') { throw 'Invalid Unicode escape.' }
                        $part = [char][Convert]::ToInt32($hex,16); $i+=4
                    } elseif ($escapes.ContainsKey($escape)) { $part=$escapes[$escape] }
                    else { throw "Invalid JSON escape '$escape'." }
                } elseif ([int]$part -lt 32) { throw 'Unescaped control character in JSON string.' }
                [void]$value.Append($part)
                $i++
            }
            if ($i -ge $Text.Length) { throw 'Unterminated JSON string.' }
            $name = $value.ToString()
            $token = $prefix + $strings.Count
            $strings.Add($token, $name)
            $next = $i+1
            while ($next -lt $Text.Length -and [char]::IsWhiteSpace($Text[$next])) { $next++ }
            if ($next -lt $Text.Length -and $Text[$next] -eq ':') {
                if (-not $frames.Count -or $frames.Peek().Kind -ne 'object') { throw 'Invalid JSON property.' }
                if (-not $frames.Peek().Names.Add($name)) { throw "Duplicate JSON property '$name'." }
            }
            [void]$output.Append('"' + $token + '"')
        } else {
            if ($character -eq '{') { $frames.Push(@{ Kind='object'; Names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }) }
            elseif ($character -eq '[') { $frames.Push(@{ Kind='array' }) }
            elseif ($character -in @('}',']')) {
                $expected = if ($character -eq '}') { 'object' } else { 'array' }
                if (-not $frames.Count -or $frames.Pop().Kind -ne $expected) { throw 'Mismatched JSON containers.' }
            }
            [void]$output.Append($character)
        }
    }
    if ($frames.Count) { throw 'Unclosed JSON container.' }
    # A property retains empty and single-item arrays on both PowerShell versions.
    $parsed = ('{"value":' + $output.ToString() + '}') | ConvertFrom-Json
    return ConvertTo-SnowMap $parsed.value $strings -AsObject:$AsObject
}

function Copy-SnowMap($Value) {
    return Read-SnowAuthorJson (ConvertTo-Json -InputObject $Value -Depth 64 -Compress)
}

function Get-SnowAuthorFile([string]$Root, [string]$Relative) {
    $relative = Get-SnowSafePath $Relative
    $path = [IO.Path]::GetFullPath((Join-Path $Root $relative))
    if (-not $path.StartsWith($Root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the source folder: $Relative"
    }
    Assert-SnowNotLinked $path
    return $path
}

function Get-SnowAuthorTarget($Definition, $Source, [string]$Variant) {
    if ($Definition -is [string]) {
        if (-not $Source.Contains('targets') -or -not $Source.targets.Contains($Definition)) { throw "Unknown target '$Definition'." }
        $Definition = $Source.targets[$Definition]
    }
    $target = Copy-SnowMap $Definition
    $text = ConvertTo-Json -InputObject $target -Depth 64 -Compress
    if ($text.Contains('{variant}')) {
        if (-not $Variant) { throw 'A target uses {variant}, but no variant was selected.' }
        $target = Read-SnowAuthorJson $text.Replace('{variant}', $Variant)
    }
    if ($target.Contains('script')) {
        foreach ($key in $target.Keys) { if ($key -notin @('script','bundle')) { throw "Unknown script target field '$key'." } }
        $bundle = if ($target.Contains('bundle')) { [string]$target.bundle } else { 'release' }
        if ($bundle -notin @('release','profile','debug')) { throw "Unsupported bundle variant '$bundle'." }
        return @{ file='preload/paks/client/initial.pak'; members=@(
            @{ format='zip'; name=('[ssl_cache]/initial_' + $bundle + '.sslbundle') },
            @{ format='sslbundle'; name=$target.script }) }
    }
    if ($target.Contains('cache')) {
        if ($target.Count -ne 1) { throw 'A cache target contains only its cache member name.' }
        return @{ file='preload/paks/client/initial.pak'; members=@(
            @{ format='zip'; name='initial.cache_block' }, @{ format='cache'; name=$target.cache }) }
    }
    if (-not $target.Contains('file')) { throw 'A target needs file, script, or cache.' }
    foreach ($key in $target.Keys) { if ($key -notin @('file','members')) { throw "Unknown target field '$key'." } }
    if (-not $target.Contains('members')) { $target.members = @() }
    return $target
}

function Get-SnowAuthorVariants($Target, $Source) {
    $definition = if ($Target -is [string]) {
        if (-not $Source.Contains('targets') -or -not $Source.targets.Contains($Target)) { throw "Unknown target '$Target'." }
        $Source.targets[$Target]
    } else { $Target }
    if ((ConvertTo-Json -InputObject $definition -Depth 64 -Compress).Contains('{variant}')) {
        if (-not $Source.Contains('variants') -or -not $Source.variants.Count) { throw 'Define variants for a {variant} target.' }
        foreach ($variant in $Source.variants) {
            if ($variant -notin @('release','profile','debug')) { throw "Unsupported variant '$variant'." }
            $variant
        }
    } else { '' }
}

function Get-SnowAuthorResource($Target, [switch]$Optional) {
    $file = Get-SnowSafePath $Target.file
    $key = $file
    if (-not $script:SnowAuthorBytes.ContainsKey($key)) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $script:SnowAuthorGameRoot $file))
        if ($Optional -and -not $Target.members.Count -and -not (Test-Path -LiteralPath $candidate)) {
            Assert-SnowNotLinked ([IO.Path]::GetDirectoryName($candidate))
            return $null
        }
        $path = Get-SnowAuthorFile $script:SnowAuthorGameRoot $file
        if ((Get-Item -LiteralPath $path).Length -gt 268435456) { throw 'Game file exceeds the 256 MiB limit.' }
        $script:SnowAuthorBytes[$key] = [IO.File]::ReadAllBytes($path)
    }
    $bytes = $script:SnowAuthorBytes[$key]
    for ($index=0; $index -lt $Target.members.Count; $index++) {
        $member = $Target.members[$index]
        $expected = switch ($member.format) { 'zip' { 'Zip' }; 'cache' { 'Cache' }; 'sslbundle' { 'Bundle' }; default { throw 'Unknown container format.' } }
        if (-not $script:SnowAuthorContainers.ContainsKey($key)) {
            if ((Get-SnowKind $bytes) -ne $expected) { throw "Wrong container format at $key." }
            $script:SnowAuthorContainers[$key] = Read-SnowContainer $bytes
        }
        $container = $script:SnowAuthorContainers[$key]
        if ($container.Kind -ne $expected) { throw "Conflicting container formats at $key." }
        $found = @($container.Entries | Where-Object { -not $_.Directory -and
            (($_.Name -ceq $member.name) -or ($expected -in @('Cache','Zip') -and $_.Path -ceq $member.name)) })
        if ($found.Count -ne 1) {
            if ($Optional -and $index -eq $Target.members.Count - 1 -and -not $found.Count) { return $null }
            throw "Expected one member '$($member.name)' in $key, found $($found.Count)."
        }
        $bytes = $found[0].Data
        $key += '|' + $member.format + ':' + $member.name
    }
    if (-not $script:SnowAuthorObserved.Contains($key)) {
        $script:SnowAuthorObserved[$key] = Get-SnowHash $bytes
    }
    return ,$bytes
}

function Get-SnowAuthorModule($Target, [string]$FunctionPattern = '') {
    if (-not $FunctionPattern) { $FunctionPattern = '*' }
    $key = (ConvertTo-Json -InputObject $Target -Depth 16 -Compress) + '|' + $FunctionPattern
    if (-not $script:SnowAuthorReports.ContainsKey($key)) {
        $bytes = Get-SnowAuthorResource $Target
        $name = if ($Target.members.Count) { $Target.members[-1].name } else { $Target.file }
        $script:SnowAuthorReports[$key] = Read-SnowModule $bytes $name $FunctionPattern
    }
    return $script:SnowAuthorReports[$key]
}

function Set-SnowAuthorHash($Map, [string]$Property, [string]$Hash) {
    if ($Map.Contains($Property) -and $Map[$Property] -cne $Hash) { throw "Explicit $Property does not match the supplied original files." }
    $Map[$Property] = $Hash
}

function Get-SnowCodeLines($Code, [string]$SourceRoot) {
    if ($Code -is [Collections.IDictionary]) {
        if ($Code.Count -ne 1 -or -not $Code.Contains('file')) { throw 'A code-file reference must contain only file.' }
        $path = Get-SnowAuthorFile $SourceRoot $Code.file
        $lines = [IO.File]::ReadAllLines($path)
    } elseif ($Code -is [array]) { $lines = $Code }
    else { throw 'Code must be an array of instruction strings, or { "file": "method.asm" }.' }
    foreach ($line in $lines) {
        if ($line -isnot [string]) { throw 'Use readable instruction strings in authoring code.' }
        $text = ($line -replace '\s+;.*$', '').Trim()
        if ($text -and -not $text.StartsWith(';')) { $text }
    }
}

function ConvertTo-SnowAuthorCode($Code, $Report, $Constants, $Slots, [switch]$Match) {
    $result = [Collections.Generic.List[object]]::new()
    $labels = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Get-SnowCodeLines $Code $script:SnowAuthorRoot)) {
        # A dependency's member slot can be used with an explicit, guarded scope.
        # Type/table indices are module-local and must never be copied this way.
        if ($line -cmatch '^(\S+)\s+member:([^/\s]+)/(.+)$') {
            $name = $Matches[1]; $alias = $Matches[2]; $reference = 'member:' + $Matches[3]
            $target = Get-SnowAuthorTarget $alias $script:SnowAuthorSource $script:SnowAuthorVariant
            Add-SnowAuthorCheck $target
            $dependency = Get-SnowAuthorModule $target
            $line = $name + ' ' + (Resolve-SnowReference $dependency $reference 'member')
        }
        if ($line -ceq 'reserve locals') {
            if (-not $Slots.Contains('@locals')) { throw 'reserve locals is available only in new methods.' }
            $line = 'reserve ' + $Slots['@locals']
        }
        if ($line -in @('return arguments','return_value arguments')) {
            if (-not $Slots.Contains('@arguments')) { throw 'return arguments is available only in new methods.' }
            $line = $line.Replace('arguments', [string]$Slots['@arguments'])
        }
        $instruction = ConvertTo-SnowInstruction $line $Report $Constants $Slots -Match:$Match
        if (-not $Match -and $instruction.Contains('label') -and -not $labels.Add($instruction.label)) { throw "Duplicate code label '$($instruction.label)'." }
        $result.Add($instruction)
    }
    if (-not $Match) {
        foreach ($instruction in $result) {
            if ($instruction.Contains('arg') -and $instruction.arg -is [Collections.IDictionary] -and $instruction.arg.Contains('label') -and -not $labels.Contains($instruction.arg.label)) {
                throw "Unknown code label '$($instruction.arg.label)'."
            }
        }
    }
    return ,$result.ToArray()
}

function Add-SnowAuthorCheck($Target) {
    $bytes = Get-SnowAuthorResource $Target
    $key = ConvertTo-Json -InputObject $Target -Depth 16 -Compress
    $script:SnowAuthorChecks[$key] = @{ target=$Target; sha256=(Get-SnowHash $bytes) }
}

function ConvertTo-SnowAuthorOperation($Definition, $Source, [string]$Variant) {
    $operation = Copy-SnowMap $Definition
    $operation.target = Get-SnowAuthorTarget $Definition.target $Source $Variant
    if ($Variant) {
        $operation.id = if ($operation.id.Contains('{variant}')) { $operation.id.Replace('{variant}', $Variant) } else { $operation.id + '-' + $Variant }
    }
    if ($operation.Contains('payload') -and $operation.payload.Contains('{variant}')) {
        if (-not $Variant) { throw 'A {variant} payload needs a variant target.' }
        $operation.payload = $operation.payload.Replace('{variant}', $Variant)
    }
    if ($operation.Contains('functionsFile')) {
        if ($operation.type -ne 'sslb.module.extend' -or $operation.Contains('functions')) { throw 'Use functions or functionsFile on a module extension, not both.' }
        if ($operation.functionsFile.Contains('{variant}') -and -not $Variant) { throw 'A {variant} functionsFile needs a variant target.' }
        $path = Get-SnowAuthorFile $script:SnowAuthorRoot $operation.functionsFile.Replace('{variant}', $Variant)
        $operation.functions = Read-SnowAuthorJson ([IO.File]::ReadAllText($path))
        if ($operation.functions -isnot [array]) { throw 'functionsFile must contain a JSON array of function insertion definitions.' }
        $operation.Remove('functionsFile')
    }
    if ($operation.Contains('editsFile')) {
        if ($operation.type -notin @('sso.edit','strings.edit','xml.edit') -or $operation.Contains('edits')) { throw 'Use edits or editsFile on a text operation, not both.' }
        $path = Get-SnowAuthorFile $script:SnowAuthorRoot $operation.editsFile
        $edits = Read-SnowAuthorJson ([IO.File]::ReadAllText($path))
        if ($edits -isnot [array] -or -not $edits.Count) { throw 'editsFile must contain a nonempty JSON array of edits.' }
        $operation.edits = $edits
        $operation.Remove('editsFile')
    }
    if ($operation.type -in @('sso.edit','strings.edit','xml.edit')) {
        foreach ($edit in $operation.edits) {
            if ($edit.before.state -eq 'present' -and -not $edit.before.Contains('value')) {
                throw "Operation '$($operation.id)' needs before.value for a present old-value guard."
            }
        }
    }
    if ($operation.type -in @('sslb.function.edit','sslb.methods.add')) {
        Add-SnowAuthorCheck @{ file='Sources/BinEditor/meta_desc_ssl.ps'; members=@() }
        $pattern = if ($operation.type -eq 'sslb.function.edit') { $operation.function.class + '::' + $operation.function.name } else { '' }
        $report = Get-SnowAuthorModule $operation.target $pattern
        Set-SnowAuthorHash $operation 'moduleContextSha256' $report.ContextSha256
        if ($operation.type -eq 'sslb.function.edit') {
            $functions = @($report.Functions | Where-Object { $_.Class -ceq $operation.function.class -and $_.Name -ceq $operation.function.name })
            if ($functions.Count -ne 1) { throw "Function $($operation.function.class)::$($operation.function.name) is missing or ambiguous." }
            $method = $functions[0]
            $minimumVersion = if ($Source.Contains('minSnowPatcherVersion')) { [version]$Source.minSnowPatcherVersion } else { [version]'1.1.0' }
            if (@($method.Instructions | Where-Object { $_.Opcode -in @(63,65,66) }).Count -gt 0 -and $minimumVersion -lt [version]'1.4.0') {
                throw 'Editing a function with array instructions needs minSnowPatcherVersion 1.4.0 or newer.'
            }
            Set-SnowAuthorHash $operation.function 'bodySha256' $method.Sha256
            $constants = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
            foreach ($constant in $method.Constants) { $constants[$constant.Name] = $constant.Index }
            foreach ($edit in $operation.edits) {
                $edit.match = ConvertTo-SnowAuthorCode $edit.match $report $constants @{} -Match
                if (-not $edit.match.Count) { throw 'A match sequence cannot be empty.' }
                $matches = 0
                for ($i=0; $i -le $method.Instructions.Count - $edit.match.Count; $i++) {
                    $same = $true
                    for ($j=0; $j -lt $edit.match.Count; $j++) {
                        if ($method.Instructions[$i+$j].Opcode -ne $edit.match[$j][0] -or $method.Instructions[$i+$j].Operand -ne $edit.match[$j][1]) { $same=$false; break }
                    }
                    if ($same) { $matches++ }
                }
                if ($matches -ne 1) { throw "Instruction match occurs $matches times in $($method.Class)::$($method.Name). Expected one." }
                $edit.code = ConvertTo-SnowAuthorCode $edit.code $report $constants @{}
            }
        } else {
            foreach ($method in $operation.methods) {
                if ($method.Contains('override')) {
                    $parts = ([string]$method.override).Split([string[]]@('::'), [StringSplitOptions]::None)
                    if ($parts.Count -ne 2) { throw 'override uses target-alias::ClassName.' }
                    $baseTarget = Get-SnowAuthorTarget $parts[0] $Source $Variant
                    Add-SnowAuthorCheck $baseTarget
                    $baseReport = Get-SnowAuthorModule $baseTarget ($parts[1] + '::' + $method.name)
                    $baseClass = @($baseReport.Classes | Where-Object { $_.Name -ceq $parts[1] })
                    if ($baseClass.Count -ne 1) { throw "Override class '$($parts[1])' is missing or ambiguous." }
                    $member = @($baseClass[0].Members | Where-Object { $_.Name -ceq $method.name -and $_.Kind -eq 2 })
                    $baseMethod = @($baseReport.Functions | Where-Object { $_.Class -ceq $parts[1] -and $_.Name -ceq $method.name })
                    if ($member.Count -ne 1 -or $baseMethod.Count -ne 1) { throw "Override method '$($method.name)' is missing or ambiguous." }
                    $method.slot = [int]$member[0].Slot
                    $signature = @{ returns=$baseReport.Symbols[[int]$baseMethod[0].ReturnTypeSymbol].Name
                        arguments=@($baseMethod[0].ParameterTypeSymbols | ForEach-Object { $baseReport.Symbols[[int]$_].Name }) }
                    if ($method.Contains('signature') -and (ConvertTo-Json -InputObject $method.signature -Compress) -cne (ConvertTo-Json -InputObject $signature -Compress)) {
                        if ($method.signature.returns -cne $signature.returns -or ($method.signature.arguments -join '|') -cne ($signature.arguments -join '|')) { throw 'Explicit signature differs from the overridden method.' }
                    }
                    $method.signature = $signature
                    $method.Remove('override')
                }
                if (-not $method.Contains('signature') -or -not $method.Contains('slot')) { throw 'A new method needs override, or an explicit slot and signature.' }
                $constants = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal); $values = [Collections.Generic.List[object]]::new()
                if (-not $method.Contains('constants')) { $method.constants = [ordered]@{} }
                if ($method.constants -isnot [Collections.IDictionary]) { throw 'New-method constants must be an object of named constants.' }
                foreach ($name in $method.constants.Keys) {
                    $value = $method.constants[$name]
                    if ($name -cnotmatch '^[A-Za-z_][A-Za-z_0-9.-]*$') { throw "Invalid constant name '$name'." }
                    if ($value -is [string]) { $value = @{ string=$value } }
                    if ($value -isnot [Collections.IDictionary]) { throw "Constant '$name' needs a string, float, or type definition." }
                    if ($value.Count -ne 1 -or @($value.Keys)[0] -notin @('string','float','type','typeSymbol')) { throw "Constant '$name' needs exactly one string, float, or type definition." }
                    if ($value.Contains('type')) { $value = @{ typeSymbol=(Resolve-SnowReference $report ('type:' + $value.type) 'type') } }
                    $constants[$name] = $values.Count; $values.Add($value)
                }
                $slots = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
                $slots['this']=0; $slots['@arguments']=$method.signature.arguments.Count+1; $slots['@locals']=0
                if ($method.Contains('parameters')) {
                    if ($method.parameters.Count -ne $method.signature.arguments.Count) { throw 'Parameter names must match the signature argument count.' }
                    for ($i=0; $i -lt $method.parameters.Count; $i++) {
                        if ($slots.Contains($method.parameters[$i])) { throw 'Duplicate argument or local name.' }
                        $slots[$method.parameters[$i]] = $i+1
                    }
                    $method.Remove('parameters')
                }
                if ($method.Contains('locals')) {
                    $slots['@locals'] = $method.locals.Count
                    for ($i=0; $i -lt $method.locals.Count; $i++) {
                        if ($slots.Contains($method.locals[$i])) { throw 'Duplicate argument or local name.' }
                        $slots[$method.locals[$i]] = -($i+1)
                    }
                    $method.Remove('locals')
                }
                $method.code = ConvertTo-SnowAuthorCode $method.code $report $constants $slots
                foreach ($row in $method.code) {
                    if ($row.Contains('arg') -and $row.arg -is [Collections.IDictionary] -and $row.arg.Contains('boundary')) { throw 'New methods use local branch labels. Match/function boundaries are only available in function edits.' }
                }
                $method.constants = $values.ToArray()
            }
        }
    } elseif ($operation.type -in @('resource.replace','resource.remove','sso.edit','sslb.module.extend')) {
        if ($operation.type -eq 'sslb.module.extend') { Add-SnowAuthorCheck @{file='Sources/BinEditor/meta_desc_ssl.ps';members=@()} }
        $bytes = Get-SnowAuthorResource $operation.target
        $indexed = $operation.type -eq 'sso.edit' -and @($operation.edits | ForEach-Object { $_.path } | Where-Object { $_ -isnot [string] }).Count -gt 0
        if ($operation.type -ne 'sso.edit' -or $operation.Contains('beforeSha256') -or $indexed) { Set-SnowAuthorHash $operation 'beforeSha256' (Get-SnowHash $bytes) }
    } elseif ($operation.type -eq 'resource.add') {
        if ($null -ne (Get-SnowAuthorResource $operation.target -Optional)) { throw 'The resource to add already exists.' }
    } elseif ($operation.type -in @('strings.edit','xml.edit')) { $null = Get-SnowAuthorResource $operation.target }
    else { throw "Unsupported operation '$($operation.type)'." }
    return ,$operation
}

function Test-SnowCompiledPayload([byte[]]$Data, [int]$Depth = 0) {
    if ($Depth -gt 8) { throw 'Payload containers are nested too deeply.' }
    $kind = Get-SnowKind $Data
    if ($kind -in @('Module','Bundle')) { return $true }
    if ($kind -in @('Zip','Cache')) {
        foreach ($entry in (Read-SnowContainer $Data).Entries) {
            if (-not $entry.Directory -and (Test-SnowCompiledPayload $entry.Data ($Depth+1))) { return $true }
        }
    }
    return $false
}
