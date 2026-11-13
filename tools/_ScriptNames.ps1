# Shared instruction and name handling. Loaded by _Common.ps1.

function Get-SnowOpcodes {
    # Names describe observed operations, not an official VM specification.
    $names = @{
        1='reserve'; 2='pop'; 3='push_null'; 4='push_int'; 5='push_bool'; 6='push_constant'; 7='load_slot'
        8='push_global'; 10='get_member'; 13='call'; 14='call_type_member'; 18='return_value'; 19='return'
        22='jump_if_false_pop'; 24='jump_if_false'; 25='jump_if_true'; 26='jump'; 27='not'
        32='add_int'; 33='add_float'; 36='subtract_int'; 37='subtract_float'; 40='multiply_float'
        48='op_48'; 49='op_49'; 50='op_50'; 53='cast'; 59='store_slot'; 60='set_member'
        68='get_index'; 71='get_index_or_default'
    }
    $result = @{}
    foreach ($number in $names.Keys) {
        $operand = if ($number -in @(10,13,14,60)) { 'member' }
            elseif ($number -in @(6,53)) { 'constant' }
            elseif ($number -eq 8) { 'global' }
            elseif ($number -in @(7,59)) { 'slot' }
            elseif ($number -in @(22,24,25,26)) { 'label' }
            else { 'integer' }
        $result[$number] = [pscustomobject]@{ Opcode=$number; Name=$names[$number]; Operand=$operand; Known=($names[$number] -notlike 'op_*') }
    }
    return $result
}

function Get-SnowTypeName($Tables, [int]$Index, [int[]]$Seen = @()) {
    if ($Index -lt 0 -or $Index -ge $Tables.Symbols.Count -or $Index -in $Seen -or $Seen.Count -gt 32) { return "symbol[$Index]" }
    $row = $Tables.Symbols[$Index]; $kind = [int](Get-SnowField $row 1)
    $layout=@{1=2;3=2;8=2;6=3;10=6;2=7;5=8;4=9;9=4}
    if(-not $layout.ContainsKey($kind) -or $row.Count -ne 2 -or @($row|Where-Object {$_.Number -ne 1 -and $_.Number -ne $layout[$kind]}).Count){return "symbol[$Index]"}
    if ($kind -eq 1) {
        $id = [int](Get-SnowField $row 2)
        if ($id -ge 0 -and $id -lt $Tables.Classes.Count) { return $Tables.Classes[$id].Name }
    } elseif ($kind -eq 3) {
        $id = [int](Get-SnowField $row 2)
        if ($id -ge 0 -and $id -lt $Tables.Native.Count) { return $Tables.Native[$id] }
    } elseif ($kind -eq 6) {
        return 'parameter:'+$Tables.Strings[[int](Get-SnowField $row 3)]
    } elseif ($kind -eq 8) {
        return 'reference<'+(Get-SnowTypeName $Tables ([int](Get-SnowField $row 2)) (@($Seen)+$Index))+'>'
    } elseif ($kind -eq 10) {
        $data=Read-SnowFields (Get-SnowField $row 6)
        return 'binding<'+(Get-SnowTypeName $Tables ([int](Get-SnowField $data 1)) (@($Seen)+$Index))+','+[string](Get-SnowField $data 2)+','+(Get-SnowTypeName $Tables ([int](Get-SnowField $data 3)) (@($Seen)+$Index))+'>'
    } elseif ($kind -in @(2,5)) {
        $data=Read-SnowFields (Get-SnowField $row $(if($kind -eq 2){7}else{8}))
        $prefix='callback'+$kind+'<'+(Get-SnowTypeName $Tables ([int](Get-SnowField $data 1)) (@($Seen)+$Index))+','+[Convert]::ToBase64String($script:SnowUtf8.GetBytes($Tables.Strings[[int](Get-SnowField $data 2)]))+','
        if($kind -eq 2){return $prefix+[Convert]::ToBase64String($script:SnowUtf8.GetBytes($Tables.Strings[[int](Get-SnowField $data 3)]))+','+[string](Get-SnowField $data 4)+','+[string](Get-SnowField $data 5)+'>'}
        return $prefix+[string](Get-SnowField $data 3)+'>'
    } elseif ($kind -eq 4) {
        $description = Read-SnowFields (Get-SnowField $row 9)
        $id = [int](Get-SnowField $description 1)
        if ($id -ge 0 -and $id -lt $Tables.Native.Count) {
            $name = $Tables.Native[$id]
            $arguments = @($description | Where-Object { $_.Number -eq 2 -and $_.Wire -eq 0 } | ForEach-Object {
                Get-SnowTypeName $Tables ([int]$_.Value) (@($Seen) + $Index)
            })
            if ($arguments.Count) { $name += '<' + ($arguments -join ',') + '>' }
            return $name
        }
    } elseif ($kind -eq 9) {
        $signature = Read-SnowFields (Get-SnowField $row 4)
        $returns = Get-SnowTypeName $Tables ([int](Get-SnowField $signature 1)) (@($Seen) + $Index)
        $arguments = @($signature | Where-Object { $_.Number -eq 2 -and $_.Wire -eq 0 } | ForEach-Object {
            Get-SnowTypeName $Tables ([int]$_.Value) (@($Seen) + $Index)
        })
        return $returns + '(' + ($arguments -join ',') + ')'
    }
    return "symbol[$Index]"
}

function Add-SnowReadableNames($Report) {
    $opcodes = Get-SnowOpcodes
    $symbols = @($Report.ModuleFields | Where-Object { $_.Field -eq 5 } | ForEach-Object {
        ,(Read-SnowFields ([Convert]::FromBase64String($_.Value)))
    })
    $referenceField = @($Report.ModuleFields | Where-Object { $_.Field -eq 4 })
    $referenceFields = @()
    if ($referenceField.Count -eq 1) { $referenceFields = Read-SnowFields ([Convert]::FromBase64String($referenceField[0].Value)) }
    $native = @($referenceFields | Where-Object { $_.Number -eq 1 -and $_.Wire -eq 2 } | ForEach-Object {
        $id = [int](Get-SnowField (Read-SnowFields $_.Value) 1)
        if ($id -lt 0 -or $id -ge $Report.Strings.Count) { throw 'Invalid native type name.' }
        $Report.Strings[$id]
    })
    $tables = @{ Symbols=$symbols; Native=$native; Classes=$Report.Classes; Strings=$Report.Strings }
    $types = @(for ($i=0; $i -lt $symbols.Count; $i++) {
        $name = Get-SnowTypeName $tables $i
        [pscustomobject]@{ Index=$i; Kind=(Get-SnowField $symbols[$i] 1); Name=$name; Reference=('type:' + $name) }
    })
    $references = [Collections.Generic.List[object]]::new()
    for ($i=0; $i -lt $types.Count; $i++) {
        $references.Add([pscustomobject]@{ Kind='type'; Table='symbols'; Index=$i; Name=$types[$i].Name; Reference=$types[$i].Reference })
    }
    for ($i=0; $i -lt $Report.Strings.Count; $i++) {
        $references.Add([pscustomobject]@{ Kind='string'; Table='strings'; Index=$i; Name=$Report.Strings[$i]; Reference=('string:' + $Report.Strings[$i]) })
    }
    $globals = @($Report.ModuleFields | Where-Object { $_.Field -eq 2 -and $_.Wire -eq 0 })
    for ($i=0; $i -lt $globals.Count; $i++) {
        $id = [int]$globals[$i].Value
        if ($id -lt 0 -or $id -ge $Report.Strings.Count) { throw 'Invalid global module name.' }
        $references.Add([pscustomobject]@{ Kind='global'; Table='globals'; Index=$i; Name=$Report.Strings[$id]; Reference=('global:' + $Report.Strings[$id]) })
    }
    $members = @($referenceFields | Where-Object { $_.Number -eq 4 -and $_.Wire -eq 2 })
    $functions = @($referenceFields | Where-Object { $_.Number -eq 2 -and $_.Wire -eq 2 })
    for ($i=0; $i -lt $functions.Count; $i++) {
        $row=Read-SnowFields $functions[$i].Value
        $global=[int](Get-SnowField $row 4)
        if($global -lt 0 -or $global -ge $globals.Count){throw 'Invalid global-function module.'}
        $name=$Report.Strings[[int]$globals[$global].Value]+'::'+$Report.Strings[[int](Get-SnowField $row 1)]
        $references.Add([pscustomobject]@{Kind='function';Table='function-references';Index=$i;Slot=[int](Get-SnowField $row 3);Name=$name;Reference=('function:'+$name);SignatureTypeSymbol=[int](Get-SnowField $row 2)})
    }
    for ($i=0; $i -lt $members.Count; $i++) {
        $row = Read-SnowFields $members[$i].Value
        $id = [int](Get-SnowField $row 2); $owner = [int](Get-SnowField $row 3)
        if ($id -lt 0 -or $id -ge $Report.Strings.Count) { throw 'Invalid member reference name.' }
        $name = (Get-SnowTypeName $tables $owner) + '::' + $Report.Strings[$id]
        $references.Add([pscustomobject]@{ Kind='member'; Table='member-references'; Index=$i; Slot=[int](Get-SnowField $row 4)
            Name=$name; Reference=('member:' + $name); OwnerTypeSymbol=$owner; SignatureTypeSymbol=(Get-SnowField $row 5 -Optional) })
    }
    foreach ($class in $Report.Classes) {
        foreach ($member in $class.Members) {
            $name = $class.Name + '::' + $member.Name
            $references.Add([pscustomobject]@{ Kind='member'; Table=('class[' + $class.Index + '].members'); Index=$member.Slot; Slot=$member.Slot
                Name=$name; Reference=('member:' + $name); OwnerTypeSymbol=$null; SignatureTypeSymbol=$member.TypeSymbol })
        }
    }
    $Report | Add-Member NoteProperty Symbols $types
    $Report | Add-Member NoteProperty References $references.ToArray()
    foreach ($method in $Report.Functions) {
        $method | Add-Member NoteProperty ReturnTypeName $(if ($null -ne $method.ReturnTypeSymbol) { Get-SnowTypeName $tables ([int]$method.ReturnTypeSymbol) } else { 'unknown' })
        $method | Add-Member NoteProperty ParameterTypeNames @($method.ParameterTypeSymbols | ForEach-Object { Get-SnowTypeName $tables ([int]$_) })
        $constants = [Collections.Generic.List[object]]::new()
        foreach ($row in @($method.Fields | Where-Object { $_.Field -eq 4 })) {
            $fields = Read-SnowFields ([Convert]::FromBase64String($row.Value))
            $kind = [int](Get-SnowField $fields 1); $value = $null; $type = 'unknown'; $name = 'c{0:D3}' -f $constants.Count
            if ($kind -eq 4) {
                $id = [int](Get-SnowField $fields 5)
                if ($id -lt 0 -or $id -ge $Report.Strings.Count) { throw 'Invalid string constant.' }
                $value = $Report.Strings[$id]; $type = 'string'
                if ($value -cmatch '^[A-Za-z_][A-Za-z_0-9.-]*$') { $name = $value }
            } elseif ($kind -eq 3) { $value = [BitConverter]::ToSingle((Get-SnowField $fields 4), 0); $type = 'float' }
            elseif ($kind -eq 1) { $value = Get-SnowField $fields 2; $type = 'integer' }
            elseif ($kind -eq 5) {
                $value = Get-SnowTypeName $tables ([int](Get-SnowField $fields 6)); $type = 'type'
                if ($value -cmatch '^[A-Za-z_][A-Za-z_0-9]*$') { $name = 'type_' + $value }
            }
            $constants.Add([pscustomobject]@{ Index=$constants.Count; Name=$name; Kind=$kind; Type=$type; Value=$value; Fields=$row.Value })
        }
        foreach ($group in @($constants | Group-Object -CaseSensitive Name | Where-Object { $_.Count -gt 1 })) {
            foreach ($constant in $group.Group) { $constant.Name = 'c{0:D3}' -f $constant.Index }
        }
        $method | Add-Member NoteProperty Constants $constants.ToArray()
        $assembly = [Collections.Generic.List[string]]::new()
        $labels = @($method.Instructions | Where-Object { $_.Opcode -in @(22,24,25,26) } | ForEach-Object { [int]$_.Operand } | Sort-Object -Unique)
        foreach ($instruction in $method.Instructions) {
            if ($instruction.Index -in $labels) { $assembly.Add(('L{0:D4}:' -f $instruction.Index)) }
            $operand = [string]$instruction.Operand; $description = ''
            if ($opcodes.ContainsKey([int]$instruction.Opcode)) {
                $operandKind = $opcodes[[int]$instruction.Opcode].Operand
                if ($operandKind -eq 'constant' -and $instruction.Operand -ge 0 -and $instruction.Operand -lt $constants.Count) {
                    $constant = $constants[[int]$instruction.Operand]; $operand = 'constant:' + $constant.Name
                    $description = $constant.Type + ' ' + (ConvertTo-Json -InputObject $constant.Value -Compress -Depth 4)
                } elseif ($operandKind -in @('member','global')) {
                    $candidates = @($Report.References | Where-Object {
                        ($_.Kind -eq $operandKind -or ($instruction.Opcode -eq 13 -and $_.Kind -eq 'function')) -and
                        (($operandKind -eq 'member' -and $_.Slot -eq $instruction.Operand) -or ($operandKind -eq 'global' -and $_.Index -eq $instruction.Operand)) })
                    $names = @($candidates | ForEach-Object { $_.Reference } | Sort-Object -Unique)
                    # A slot is interpreted on the receiver at runtime. Even one
                    # available name is only a candidate without receiver analysis.
                    if ($operandKind -eq 'global' -and $names.Count -eq 1) { $operand = $names[0] }
                    elseif ($names.Count -gt 0) { $description = 'candidates: ' + ($names -join ', ') }
                } elseif ($operandKind -eq 'label') { $operand = 'L{0:D4}' -f $instruction.Operand }
            }
            $line = $instruction.Mnemonic + ' ' + $operand
            $instruction | Add-Member NoteProperty Assembly $line
            $instruction | Add-Member NoteProperty Description $description
            $assembly.Add($line + $(if ($description) { ' ; ' + $description } else { '' }))
        }
        if ($method.Instructions.Count -in $labels) { $assembly.Add(('L{0:D4}:' -f $method.Instructions.Count)) }
        $method | Add-Member NoteProperty Assembly $assembly.ToArray()
    }
}

function Resolve-SnowReference($Report, [string]$Reference, [string]$Kind) {
    $matches = @($Report.References | Where-Object { $_.Kind -eq $Kind -and $_.Reference -ceq $Reference })
    $numbers = @($matches | ForEach-Object { if ($Kind -in @('member','function')) { $_.Slot } else { $_.Index } } | Sort-Object -Unique)
    if ($numbers.Count -ne 1) { throw "Reference '$Reference' has $($numbers.Count) possible values in $($Report.Module). Use Resolve-ScriptNames.ps1 to inspect it." }
    return [int]$numbers[0]
}

function ConvertTo-SnowInstruction($Line, $Report, $Constants = @{}, $Slots = @{}, [switch]$Match) {
    if ($Line -isnot [string]) { throw 'Readable code uses one instruction string per line.' }
    $text = $Line.Trim()
    if ($text -match '^([A-Za-z_][A-Za-z_0-9]*):$') {
        if ($Match) { throw 'Match sequences cannot contain label declarations.' }
        return @{ label=$Matches[1] }
    }
    if ($text -notmatch '^(\S+)(?:\s+(.+))?$') { throw 'An instruction is blank.' }
    $name = $Matches[1]; $operand = if ($Matches.ContainsKey(2)) { $Matches[2] } else { '0' }
    $opcodes = Get-SnowOpcodes
    $found = @($opcodes.Values | Where-Object { $_.Name -ceq $name })
    if ($found.Count -ne 1) { throw "Unknown instruction '$name'. Use Resolve-ScriptNames.ps1 -Instruction '*' to list supported names." }
    $op = [int]$found[0].Opcode; $kind = $found[0].Operand; $arg = $null
    if ($kind -eq 'label') {
        if ($Match) {
            if ($operand -cmatch '^L([0-9]+)$') { $arg = [int]$Matches[1] }
            elseif ($operand -match '^[0-9]+$') { $arg = [int]$operand }
            else { throw "Match branch '$operand' must use an exported L0000 label or original instruction number." }
        } elseif ($operand -in @('match.start','match.end','function.end')) { $arg = @{ boundary=$operand } }
        elseif ($operand -cmatch '^[A-Za-z_][A-Za-z_0-9]*$') { $arg = @{ label=$operand } }
        else { throw "New branch '$operand' must use a label or match/function boundary." }
    } elseif ($operand -cmatch '^constant:(.+)$' -and $kind -eq 'constant') {
        $key = $Matches[1]
        if (-not $Constants.Contains($key)) { throw "Unknown constant '$key'." }
        $arg = [int]$Constants[$key]
    } elseif ($operand -cmatch '^slot:(.+)$' -and $kind -eq 'slot') {
        $key = $Matches[1]
        if (-not $Slots.Contains($key)) { throw "Unknown local or argument '$key'." }
        $arg = [int]$Slots[$key]
    } elseif ($op -eq 13 -and $operand.StartsWith('function:',[StringComparison]::Ordinal)) {
        $arg = Resolve-SnowReference $Report $operand 'function'
    } elseif ($operand.StartsWith($kind + ':', [StringComparison]::Ordinal) -and $kind -in @('member','global')) {
        $arg = Resolve-SnowReference $Report $operand $kind
    } elseif ($op -eq 5 -and $operand -in @('true','false')) { $arg = [int]($operand -eq 'true') }
    elseif ($operand -match '^-?[0-9]+$') { $arg = [int]::Parse($operand, [Globalization.CultureInfo]::InvariantCulture) }
    else { throw "Invalid $kind operand '$operand' for $name." }
    if ($arg -is [int] -and ($arg -lt -8388608 -or $arg -gt 8388607)) { throw 'Instruction operand exceeds the VM encoding range.' }
    if ($Match) { return ,@($op,$arg) }
    return @{ op=$op; arg=$arg }
}
