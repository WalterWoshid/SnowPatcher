# Shared bytecode inspection helpers. Loaded by _Common.ps1.

function Read-SnowVarint([byte[]]$Data, [ref]$Position) {
    [uint64]$value = 0
    for ($shift = 0; $shift -lt 70; $shift += 7) {
        if ($Position.Value -ge $Data.Length) { throw 'Truncated protobuf varint.' }
        [byte]$part = $Data[$Position.Value]; $Position.Value += 1
        if ($shift -eq 63 -and $part -gt 1) { throw 'Oversized protobuf varint.' }
        $value = $value -bor (([uint64]($part -band 127)) -shl $shift)
        if ($part -lt 128) { return $value }
    }
    throw 'Oversized protobuf varint.'
}

function Read-SnowFields([byte[]]$Data) {
    $fields = [Collections.Generic.List[object]]::new()
    $position = 0
    while ($position -lt $Data.Length) {
        $start = $position
        $key = Read-SnowVarint $Data ([ref]$position)
        $number = $key -shr 3; $wire = $key -band 7
        if ($number -eq 0) { throw 'Invalid protobuf field number.' }
        if ($wire -eq 0) { $value = Read-SnowVarint $Data ([ref]$position) }
        else {
            if ($wire -eq 2) { $length = Read-SnowVarint $Data ([ref]$position) }
            elseif ($wire -eq 1) { $length = 8 }
            elseif ($wire -eq 5) { $length = 4 }
            else { throw "Unsupported protobuf wire type: $wire" }
            if ($length -gt ($Data.Length - $position)) { throw 'Truncated protobuf field.' }
            $value = [byte[]]::new([int]$length)
            [Array]::Copy($Data, $position, $value, 0, [int]$length)
            $position += [int]$length
        }
        $fields.Add([pscustomobject]@{ Number=[int]$number; Wire=[int]$wire; Value=$value; Offset=$start; Length=($position-$start) })
    }
    return ,$fields.ToArray()
}

function Get-SnowField($Fields, [int]$Number, [switch]$Optional) {
    $found = @($Fields | Where-Object { $_.Number -eq $Number })
    if ($Optional -and $found.Count -eq 0) { return $null }
    if ($found.Count -ne 1) { throw "Expected one protobuf field $Number, found $($found.Count)." }
    return ,$found[0].Value
}

function ConvertTo-SnowFieldReport($Fields) {
    return ,@(foreach ($field in $Fields) {
        $value = if ($field.Wire -eq 0) { $field.Value } else { [Convert]::ToBase64String($field.Value) }
        [pscustomobject]@{ Field=$field.Number; Wire=$field.Wire; Value=$value }
    })
}

function Read-SnowModule([byte[]]$Data, [string]$Name, [string]$FunctionPattern = '*') {
    if ((Get-SnowKind $Data) -ne 'Module' -or $Data.Length -lt 16 -or [BitConverter]::ToUInt32($Data, 12) -ne ($Data.Length - 16)) {
        throw "Unsupported SSLB module header or payload length: $Name"
    }
    $payload = [byte[]]::new($Data.Length - 16)
    [Array]::Copy($Data, 16, $payload, 0, $payload.Length)
    $fields = Read-SnowFields $payload
    $strings = @($fields | Where-Object { $_.Number -eq 6 } | ForEach-Object { $script:SnowUtf8.GetString($_.Value) })
    $bodies = @($fields | Where-Object { $_.Number -eq 8 })
    $symbols = @($fields | Where-Object { $_.Number -eq 5 })
    $classDefinitions = @($fields | Where-Object { $_.Number -eq 7 })
    $classes = [Collections.Generic.List[object]]::new()
    $classSymbols = @{}
    for ($i = 0; $i -lt $symbols.Count; $i++) {
        $symbolFields = Read-SnowFields $symbols[$i].Value
        if ((Get-SnowField $symbolFields 1) -eq 1) {
            $classIndex = [int](Get-SnowField $symbolFields 2)
            if ($classIndex -lt 0 -or $classIndex -ge $classDefinitions.Count) { throw 'Invalid local class symbol.' }
            if (-not $classSymbols.ContainsKey($classIndex)) { $classSymbols[$classIndex] = @() }
            $classSymbols[$classIndex] += @($i)
        }
    }
    $names = [Collections.Generic.List[object]]::new()
    $namedIndices = [Collections.Generic.HashSet[int]]::new()
    foreach ($class in $classDefinitions) {
        $classFields = Read-SnowFields $class.Value
        $classId = Get-SnowField $classFields 1
        if ($classId -ge $strings.Count) { throw 'Invalid class name reference.' }
        $members = [Collections.Generic.List[object]]::new()
        foreach ($member in @($classFields | Where-Object { $_.Number -eq 3 })) {
            $row = Read-SnowFields $member.Value
            $type = Get-SnowField $row 3
            $memberName = Get-SnowField $row 1
            if ($memberName -ge $strings.Count) { throw 'Invalid member name reference.' }
            $typeInfo = Get-SnowField $row 8 -Optional
            $typeSymbol = if ($null -ne $typeInfo) { Get-SnowField (Read-SnowFields $typeInfo) 1 } else { $null }
            $members.Add([pscustomobject]@{ Name=$strings[$memberName]; Slot=(Get-SnowField $row 2); Kind=$type;
                TypeSymbol=$typeSymbol; FunctionIndex=$(if ($type -eq 2) { Get-SnowField $row 4 } else { $null }) })
            if ($type -ne 2) { continue }
            $nameId = Get-SnowField $row 1; $index = [int](Get-SnowField $row 4)
            if ($nameId -ge $strings.Count -or $index -lt 0 -or $index -ge $bodies.Count) { throw 'Invalid function reference.' }
            $names.Add([pscustomobject]@{ Class=$strings[$classId]; Name=$strings[$nameId]; Index=$index; Signature=$row })
            [void]$namedIndices.Add($index)
        }
        $ownSymbols = if ($classSymbols.ContainsKey($classes.Count)) { @($classSymbols[$classes.Count]) } else { @() }
        $classes.Add([pscustomobject]@{ Index=$classes.Count; Name=$strings[$classId]; TypeSymbols=[int[]]@($ownSymbols | Where-Object { $null -ne $_ });
            BaseTypeSymbol=(Get-SnowField $classFields 2 -Optional); Members=$members.ToArray() })
    }
    for ($i = 0; $i -lt $bodies.Count; $i++) {
        if (-not $namedIndices.Contains($i)) { $names.Add([pscustomobject]@{ Class=''; Name=('function_' + $i); Index=$i; Signature=@() }) }
    }
    $functions = [Collections.Generic.List[object]]::new()
    $mnemonics = Get-SnowOpcodes
    foreach ($function in $names) {
        $qualified = $function.Class + '::' + $function.Name
        if ($function.Name -notlike $FunctionPattern -and $qualified -notlike $FunctionPattern) { continue }
        $raw = $bodies[$function.Index].Value
        $functionFields = Read-SnowFields $raw
        $functionType = Get-SnowField $functionFields 1 -Optional
        $returnType = $null; $argumentTypes = @(); $debugName = ''; $parameterNames = @()
        if ($null -ne $functionType) {
            if ($functionType -ge $symbols.Count) { throw 'Invalid function type symbol.' }
            $symbolFields = Read-SnowFields $symbols[$functionType].Value
            if ((Get-SnowField $symbolFields 1) -eq 9) {
                $signature = Read-SnowFields (Get-SnowField $symbolFields 4)
                $returnType = Get-SnowField $signature 1
                $argumentTypes = @($signature | Where-Object { $_.Number -eq 2 -and $_.Wire -eq 0 } | ForEach-Object { $_.Value })
            }
        }
        $debug = Get-SnowField $functionFields 2 -Optional
        if ($null -ne $debug) {
            $debugFields = Read-SnowFields $debug
            $debugId = Get-SnowField $debugFields 1 -Optional
            if ($null -ne $debugId -and $debugId -lt $strings.Count) { $debugName = $strings[$debugId] }
            $parameterNames = @($debugFields | Where-Object { $_.Number -eq 2 -and $_.Wire -eq 2 } | ForEach-Object {
                $parameter = Get-SnowField (Read-SnowFields $_.Value) 1
                if ($parameter -ge $strings.Count) { throw 'Invalid parameter name reference.' }
                $strings[$parameter]
            })
        }
        $code = Get-SnowField $functionFields 5 -Optional
        $instructions = [Collections.Generic.List[object]]::new()
        if ($null -ne $code) {
            foreach ($instruction in (Read-SnowFields $code)) {
                if ($instruction.Number -ne 1 -or $instruction.Wire -ne 0 -or $instruction.Value -gt [long]::MaxValue) {
                    throw "Unsupported instruction encoding: $qualified"
                }
                [long]$word = [long]$instruction.Value - 2147483648L
                $opcode = [int]($word -band 255)
                $mnemonic = if ($mnemonics.ContainsKey($opcode)) { $mnemonics[$opcode].Name } else { 'op_' + $opcode }
                $instructions.Add([pscustomobject]@{ Index=$instructions.Count; Opcode=$opcode; Operand=($word -shr 8); Mnemonic=$mnemonic })
            }
        }
        $functions.Add([pscustomobject]@{
            Class=$function.Class; Name=$function.Name; Index=$function.Index; Sha256=(Get-SnowHash $raw)
            FunctionTypeSymbol=$functionType; ReturnTypeSymbol=$returnType; ParameterTypeSymbols=[long[]]$argumentTypes
            DebugName=$debugName; ParameterNames=[string[]]$parameterNames
            SignatureFields=(ConvertTo-SnowFieldReport $function.Signature)
            Fields=(ConvertTo-SnowFieldReport $functionFields)
            RawBase64=[Convert]::ToBase64String($raw); Instructions=$instructions.ToArray()
        })
    }
    # Function instructions refer to these module-level tables. Export them with the functions.
    $context = [IO.MemoryStream]::new()
    try {
        foreach ($field in $fields) { if ($field.Number -ne 8) { $context.Write($payload, $field.Offset, $field.Length) } }
        $contextHash = Get-SnowHash $context.ToArray()
    } finally { $context.Dispose() }
    $report = [pscustomobject]@{
        Module=$Name; Sha256=(Get-SnowHash $Data); ContextSha256=$contextHash
        HeaderBase64=[Convert]::ToBase64String($Data[0..15]); Strings=$strings
        Classes=$classes.ToArray()
        ModuleFields=(ConvertTo-SnowFieldReport @($fields | Where-Object { $_.Number -ne 8 -and $_.Number -ne 6 }))
        Functions=$functions.ToArray()
    }
    Add-SnowReadableNames $report
    return $report
}

function Get-SnowModules([byte[]]$Data, [string]$Name, [string]$ModulePattern = '*', [int]$Depth = 0) {
    if ($Depth -gt 8) { throw 'Containers are nested too deeply.' }
    $kind = Get-SnowKind $Data
    if ($kind -eq 'Module') {
        if ($Name -like $ModulePattern) { [pscustomobject]@{ Name=$Name; Data=$Data } }
    } elseif ($kind -in @('Bundle', 'Zip', 'Cache')) {
        $container = Read-SnowContainer $Data
        foreach ($entry in $container.Entries) {
            if (-not $entry.Directory -and (Get-SnowKind $entry.Data) -in @('Module', 'Bundle', 'Cache')) {
                Get-SnowModules $entry.Data $entry.Name $ModulePattern ($Depth + 1)
            }
        }
    }
}
