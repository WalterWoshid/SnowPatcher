# Shared portable-package generation. No game code is included in these guards.

function Get-SnowPortableMember($Report, [string]$Name) {
    if (-not $Report.PSObject.Properties['PortableMembers']) { $Report | Add-Member NoteProperty PortableMembers @{} }
    if ($Report.PortableMembers.ContainsKey($Name)) { return $Report.PortableMembers[$Name] }
    $parts = $Name.Split([string[]]@('::'), [StringSplitOptions]::None)
    if ($parts.Count -ne 2) { throw 'A portable member needs Class::Name.' }
    $refs = @($Report.References | Where-Object { $_.Kind -eq 'member' -and $_.Table -eq 'member-references' -and $_.Name -ceq $Name })
    $owners = @($Report.Classes | Where-Object Name -CEQ $parts[0])
    if ($owners.Count -gt 1) { throw "Ambiguous class: $($parts[0])" }
    $decl = @($owners | ForEach-Object { $_.Members | Where-Object Name -CEQ $parts[1] })
    $sig = ''; $parent = ''; $extra = ''
    $signatures = @($refs | ForEach-Object { if ($null -ne $_.SignatureTypeSymbol) { $Report.Symbols[[int]$_.SignatureTypeSymbol].Name } else { '' } } | Sort-Object -CaseSensitive -Unique)
    if ($signatures.Count -gt 1) { throw "Overloaded reference needs an adapter: $Name" }
    if ($decl.Count -eq 1) {
        $m=$decl[0]; $slot=[int]$m.Slot; $kind=[int]$m.Kind
        $classFields=Read-SnowFields ([Convert]::FromBase64String(@($Report.ModuleFields | Where-Object Field -EQ 7)[$owners[0].Index].Value))
        $rows=@($classFields | Where-Object Number -EQ 3 | ForEach-Object { ,(Read-SnowFields $_.Value) })
        $row=@($rows | Where-Object { $Report.Strings[[int](Get-SnowField $_ 1)] -ceq $parts[1] })[0]
        $extraBytes=[IO.MemoryStream]::new()
        try {
            $raw=[byte[]]@($classFields | Where-Object Number -EQ 3)[$([array]::IndexOf(@($owners[0].Members.Name),$parts[1]))].Value
            foreach($f in $row) { if($f.Number -notin @(1,2,3,8) -and -not ($kind -eq 2 -and $f.Number -eq 4)) { $extraBytes.Write($raw,$f.Offset,$f.Length) } }
            $extra=([BitConverter]::ToString($extraBytes.ToArray())).Replace('-','')
        } finally { $extraBytes.Dispose() }
        if ($kind -eq 2) {
            $body=@($Report.Functions | Where-Object Index -EQ ([int]$m.FunctionIndex))[0]
            $sig=$Report.Symbols[[int]$body.FunctionTypeSymbol].Name
        } elseif ($null -ne $m.TypeSymbol) { $sig=$Report.Symbols[[int]$m.TypeSymbol].Name }
        elseif ($signatures.Count) { $sig=$signatures[0] }
        if ($null -ne $owners[0].BaseTypeSymbol) { $parent=$Report.Symbols[[int]$owners[0].BaseTypeSymbol].Name }
        if (@($refs | Where-Object Slot -NE $slot).Count) { throw "Inconsistent member slot: $Name" }
    } elseif ($decl.Count -gt 1) { throw "Ambiguous member: $Name" }
    else {
        $slots=@($refs.Slot | Sort-Object -Unique)
        $table=Read-SnowFields ([Convert]::FromBase64String(@($Report.ModuleFields | Where-Object Field -EQ 4)[0].Value))
        $entries=@($table | Where-Object Number -EQ 4)
        $kinds=@(foreach($r in $refs){$fields=Read-SnowFields $entries[[int]$r.Index].Value; Get-SnowField $fields 1}) | Sort-Object -Unique
        $kinds=@($kinds)
        if ($slots.Count -ne 1 -or $kinds.Count -ne 1) { throw "Missing or ambiguous member: $Name" }
        $slot=[int]$slots[0]; $kind=[int]$kinds[0]; if($signatures.Count){$sig=$signatures[0]}
    }
    if($sig -match 'symbol\[' -or $parent -match 'symbol\['){throw "Member $Name has an unsupported type. Use -Strict until its layout has an adapter."}
    $contract=(ConvertTo-SnowB64 $parts[0])+'|'+(ConvertTo-SnowB64 $parts[1])+'|'+$kind+'|'+(ConvertTo-SnowB64 $sig)+'|'+(ConvertTo-SnowB64 $parent)+'|'+$extra
    $result=@{ slot=$slot; contract=(Get-SnowHash $script:SnowUtf8.GetBytes($contract)) }
    $Report.PortableMembers[$Name]=$result
    return $result
}
function ConvertTo-SnowB64([string]$Text) { return [Convert]::ToBase64String($script:SnowUtf8.GetBytes($Text)) }
function Get-SnowPortableGlobalFunction($Report,[string]$Name) {
    $found=@($Report.References|Where-Object {$_.Kind -eq 'function' -and $_.Name -ceq $Name})
    $slots=@($found.Slot|Sort-Object -Unique);$signatures=@($found|ForEach-Object {$Report.Symbols[[int]$_.SignatureTypeSymbol].Name}|Sort-Object -CaseSensitive -Unique)
    if($slots.Count -ne 1 -or $signatures.Count -ne 1){throw "Global function is missing or ambiguous: $Name"}
    return @{slot=[int]$slots[0];contract=(Get-SnowHash $script:SnowUtf8.GetBytes((ConvertTo-SnowB64 $Name)+'|'+(ConvertTo-SnowB64 $signatures[0])))}
}
function Get-SnowPortableConstant($Report, $Method, [int]$Index) {
    if($Index -lt 0 -or $Index -ge $Method.Constants.Count){throw 'Invalid portable constant.'}
    $c=$Method.Constants[$Index]
    switch($c.Kind) {
        1 { return 'i:'+[string]$c.Value }
        3 { $f=Read-SnowFields ([Convert]::FromBase64String($c.Fields)); return 'f:'+([BitConverter]::ToString((Get-SnowField $f 4))).Replace('-','') }
        4 { return 's:'+(ConvertTo-SnowB64 $c.Value) }
        5 { return 't:'+(ConvertTo-SnowB64 $c.Value) }
        6 {
            # Preserve opaque callback constants only while every shared table is unchanged.
            return 'opaque:'+$Report.ContextSha256+':'+$c.Fields
        }
        default { throw "Constant kind $($c.Kind) needs a relocation adapter." }
    }
}
function Get-SnowPortableFunctionHash($Report, $Method) {
    $opcodes=Get-SnowOpcodes
    $identity=$Method.Class+'::'+$Method.Name
    $flags=@($Method.Fields|Where-Object Field -EQ 3)
    if($flags.Count -ne 1 -or @($Method.Fields|Where-Object {$_.Field -notin @(1,2,3,4,5)}).Count){throw 'Unknown portable function metadata.'}
    $lines=[Collections.Generic.List[string]]::new()
    $lines.Add('owner:'+(Get-SnowPortableMember $Report $identity).contract)
    $lines.Add('sig:'+(ConvertTo-SnowB64 $Report.Symbols[[int]$Method.FunctionTypeSymbol].Name))
    $lines.Add('flags:'+[string]$flags[0].Value)
    foreach($i in $Method.Instructions) {
        $op=[int]$i.Opcode; $arg=[int]$i.Operand
        if($op -notin @(1,2,3,4,5,6,7,8,10,13,14,15,18,19,20,21,22,24,25,26,27,30,32,33,36,37,40,43,48,49,50,53,56,57,58,59,60,63,65,68,69,71,72)){throw "Opcode $op needs a relocation adapter."}
        if($op -in @(21,30,43,56,63,65,69,72) -and $arg -ne 0){throw "Unsupported scalar/array instruction flags."}
        if($op -in @(10,13,14,60)) {
            $names=@($Report.References|Where-Object {$_.Kind -eq 'member' -and $_.Slot -eq $arg}|ForEach-Object Name|Sort-Object -CaseSensitive -Unique)
            $functionNames=@($Report.References|Where-Object {$_.Kind -eq 'function' -and $_.Slot -eq $arg}|ForEach-Object Name|Sort-Object -CaseSensitive -Unique)
            if(-not $names.Count -and -not $functionNames.Count){throw "Dispatch slot $arg has no named identity."}
            $contracts=[string[]]@(@($names|ForEach-Object { $_+'='+(Get-SnowPortableMember $Report $_).contract })+@($functionNames|ForEach-Object {'global:'+$_+'='+(Get-SnowPortableGlobalFunction $Report $_).contract}))
            [Array]::Sort($contracts,[StringComparer]::Ordinal)
            $value='m:'+($contracts -join ';')
        } elseif($opcodes.ContainsKey($op) -and $opcodes[$op].Operand -eq 'constant') { $value='c:'+(Get-SnowPortableConstant $Report $Method $arg) }
        elseif($op -eq 8){$global=@($Report.References|Where-Object {$_.Kind -eq 'global' -and $_.Index -eq $arg});if($global.Count -ne 1){throw 'Invalid global.'};$value='g:'+(ConvertTo-SnowB64 $global[0].Name)}
        else { $value='n:'+[string]$arg }
        $lines.Add([string]$op+':'+$value)
    }
    return Get-SnowHash $script:SnowUtf8.GetBytes(($lines -join "`n"))
}
function Add-SnowPortableFunction($Target,[string]$Class,[string]$Name,[string]$Mode='body',[bool]$Read=$true) {
    $key=(ConvertTo-Json -InputObject $Target -Depth 16 -Compress)+'|'+$Class+'::'+$Name
    if($script:SnowPortableFunctions.Contains($key)){
        $previous=$script:SnowPortableFunctions[$key]
        $Read=$Read -or $previous.read
        if($previous.mode -eq 'body' -or $Mode -eq 'signature'){$previous.read=$Read;return}
    }
    $report=Get-SnowAuthorModule $Target
    $method=@($report.Functions|Where-Object {$_.Class -ceq $Class -and $_.Name -ceq $Name})
    if($method.Count -ne 1){throw "Missing or ambiguous function: $Class::$Name"}
    $hash=if($Mode -eq 'signature'){(Get-SnowPortableMember $report ($Class+'::'+$Name)).contract}else{Get-SnowPortableFunctionHash $report $method[0]}
    $script:SnowPortableFunctions[$key]=@{target=$Target;class=$Class;name=$Name;mode=$Mode;read=$Read;sha256=$hash}
}

function Get-SnowPortableFragment([string]$Text,[string[]]$Path) {
    $pattern='"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/|[^\s{}=\[\],"]+|[{}=\[\],]'
    $current=$Text
    foreach($key in $Path) {
        $found=[Collections.Generic.List[object]]::new();$tokens=@([regex]::Matches($current,$pattern)|Where-Object {-not $_.Value.StartsWith('//') -and -not $_.Value.StartsWith('/*')});$depth=0
        $expected=if($tokens.Count -and $tokens[0].Value -eq '{'){1}else{0}
        for($i=0;$i -lt $tokens.Count-2;$i++) {
            $v=$tokens[$i].Value
            if($v -in @('{','[')){$depth++;continue};if($v -in @('}',']')){$depth--;continue}
            $property=if($v.StartsWith('"')){Read-SnowAuthorJson $v}else{$v}
            if($depth -ne $expected -or $property -cne $key -or $tokens[$i+1].Value -ne '='){continue}
            $start=$i+2;$end=$start;$nested=0
            if($tokens[$start].Value -in @('{','[')){
                for($end=$start;$end -lt $tokens.Count;$end++){
                    if($tokens[$end].Value -in @('{','[')){$nested++};if($tokens[$end].Value -in @('}',']')){$nested--};if($nested -eq 0){break}
                }
            }
            $found.Add($current.Substring($tokens[$start].Index,$tokens[$end].Index+$tokens[$end].Length-$tokens[$start].Index))
        }
        if($found.Count -eq 0){return $null};if($found.Count -ne 1){throw 'Ambiguous native metadata path.'};$current=[string]$found[0]
    }
    return $current
}
function Add-SnowPortableNative([string]$Class,[string]$Member) {
    # Extract the named top-level record first; scanning the complete metadata for
    # every property would be needlessly expensive in Windows PowerShell.
    if(-not $script:SnowPortableNativeRecords.ContainsKey($Class)) {
        $match=[regex]::Matches($script:SnowPortableNativeText,'(?m)^'+[regex]::Escape($Class)+'\s*=')
        if($match.Count -ne 1){$script:SnowPortableNativeRecords[$Class]=$null}
        else {
            $start=$match[0].Index;$tail=$script:SnowPortableNativeText.Substring($start)
            $next=[regex]::Match($tail.Substring($match[0].Length),'(?m)^[A-Za-z_][A-Za-z_0-9]*\s*=')
            $length=if($next.Success){$match[0].Length+$next.Index}else{$tail.Length}
            $script:SnowPortableNativeRecords[$Class]=$tail.Substring(0,$length)
        }
    }
    $record=$script:SnowPortableNativeRecords[$Class];if($null -eq $record){return}
    foreach($path in @(@($Class,'__type'),@($Class,'__members',$Member))) {
        $key=$path -join '/';if($script:SnowPortableNative.Contains($key)){continue}
        $fragment=Get-SnowPortableFragment $record $path
        if($null -eq $fragment){continue}
        $script:SnowPortableNative[$key]=@{path=[string[]]$path;sha256=(Get-SnowPortableTokenHash $fragment)}
    }
}
function Get-SnowPortableTokenHash([string]$Fragment) {
    $normalized=[Text.StringBuilder]::new()
    foreach($token in [regex]::Matches($Fragment,'"(?:\\.|[^"\\])*"|//[^\r\n]*|/\*[\s\S]*?\*/|[^\s{}=\[\],"]+|[{}=\[\],]')) {
        if($token.Value.StartsWith('//') -or $token.Value.StartsWith('/*')){continue}
        if($token.Value.StartsWith('"')){[void]$normalized.Append('s:'+(ConvertTo-SnowB64 (Read-SnowAuthorJson $token.Value))+';')}
        else{[void]$normalized.Append('t:'+$token.Value+';')}
    }
    return Get-SnowHash $script:SnowUtf8.GetBytes($normalized.ToString())
}
function Add-SnowPortableBinding($Operation,$Path,[string]$Kind,$Target,[string]$Name) {
    $binding=@{operation=$Operation.id;path=$Path;kind=$Kind;target=$Target;name=$Name}
    if($Kind -eq 'member') {
        $binding.contract=(Get-SnowPortableMember (Get-SnowAuthorModule $Target) $Name).contract
        $parts=$Name.Split([string[]]@('::'),[StringSplitOptions]::None);Add-SnowPortableNative $parts[0] $parts[1]
    }
    elseif($Kind -eq 'function'){$binding.contract=(Get-SnowPortableGlobalFunction (Get-SnowAuthorModule $Target) $Name).contract}
    $script:SnowPortableOperands.Add($binding)
}
function Add-SnowPortableCode($SourceCode,$Rows,$Prefix,$Operation,[bool]$FunctionConstants) {
    $opcodes=Get-SnowOpcodes
    $lines=@(Get-SnowCodeLines $SourceCode $script:SnowAuthorRoot);$report=Get-SnowAuthorModule $Operation.target
    if($lines.Count -ne $Rows.Count){throw 'Portable instruction mapping is inconsistent.'}
    for($i=0;$i -lt $Rows.Count;$i++) {
        $row=$Rows[$i];if($row -is [array]){$op=[int]$row[0];$arg=$row[1];$path=@($Prefix)+@($i,1)}
        else{if($row.Contains('label')){continue};$op=[int]$row.op;$arg=$row.arg;$path=@($Prefix)+@($i,'arg')}
        if($op -in @(10,13,14,60) -or ($op -eq 4 -and $lines[$i] -cmatch '^\S+\s+member:')) {
            $target=$Operation.target
            if($op -eq 13 -and $lines[$i] -cmatch '^\S+\s+function:(.+)$') { Add-SnowPortableBinding $Operation $path 'function' $target $Matches[1];continue }
            if($lines[$i] -cmatch '^\S+\s+member:([^/\s]+)/(.+)$'){$target=Get-SnowAuthorTarget $Matches[1] $script:SnowAuthorSource $script:SnowAuthorVariant;$name=$Matches[2]}
            elseif($lines[$i] -cmatch '^\S+\s+member:(.+)$'){$name=$Matches[1]}
            else {
                $names=@($report.References|Where-Object {$_.Kind -eq 'member' -and $_.Slot -eq $arg}|ForEach-Object Name|Sort-Object -Unique)
                if($names.Count -ne 1){throw "Use a named member for portable instruction '$($lines[$i])', or build with -Strict."};$name=$names[0]
            }
            Add-SnowPortableBinding $Operation $path 'member' $target $name
        } elseif($op -eq 8){$name=@($report.References|Where-Object {$_.Kind -eq 'global' -and $_.Index -eq $arg})[0].Name;Add-SnowPortableBinding $Operation $path 'global' $Operation.target $name}
        elseif($FunctionConstants -and $opcodes.ContainsKey($op) -and $opcodes[$op].Operand -eq 'constant') {
            $method=@($report.Functions|Where-Object {$_.Class -ceq $Operation.function.class -and $_.Name -ceq $Operation.function.name})[0]
            Add-SnowPortableBinding $Operation $path 'constant' $Operation.target (Get-SnowPortableConstant $report $method ([int]$arg))
        }
    }
}
function Add-SnowPortableOperation($Definition,$Operation) {
    if($Operation.type -eq 'sslb.function.edit') {
        Add-SnowPortableFunction $Operation.target $Operation.function.class $Operation.function.name 'body' $false
        for($e=0;$e -lt $Operation.edits.Count;$e++){
            Add-SnowPortableCode $Definition.edits[$e].match $Operation.edits[$e].match @('edits',$e,'match') $Operation $true
            Add-SnowPortableCode $Definition.edits[$e].code $Operation.edits[$e].code @('edits',$e,'code') $Operation $true
        }
    } elseif($Operation.type -eq 'sslb.methods.add') {
        Add-SnowPortableFunction $Operation.target $Operation.class $Operation.templateMember 'signature' $false
        for($m=0;$m -lt $Operation.methods.Count;$m++){
            $original=$Definition.methods[$m];$method=$Operation.methods[$m]
            if(-not $original.Contains('override')){throw 'Portable added methods require a named override. Use -Strict for numeric slots.'}
            $parts=$original.override.Split([string[]]@('::'),[StringSplitOptions]::None);$target=Get-SnowAuthorTarget $parts[0] $script:SnowAuthorSource $script:SnowAuthorVariant
            Add-SnowPortableBinding $Operation @('methods',$m,'slot') 'member' $target ($parts[1]+'::'+$method.name)
            Add-SnowPortableFunction $target $parts[1] $method.name
            for($c=0;$c -lt $method.constants.Count;$c++){
                if($method.constants[$c].Contains('typeSymbol')){Add-SnowPortableBinding $Operation @('methods',$m,'constants',$c,'typeSymbol') 'type' $Operation.target (Get-SnowAuthorModule $Operation.target).Symbols[[int]$method.constants[$c].typeSymbol].Name}
            }
            Add-SnowPortableCode $original.code $method.code @('methods',$m,'code') $Operation $false
        }
    }
}
