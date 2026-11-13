<#
.SYNOPSIS
Inspect or export compiled functions from SSLB modules and containers.
.EXAMPLE
.\Export-Functions.ps1 '.\initial_release.sslbundle' -Module '*popup_refuel*' -List
.EXAMPLE
.\Export-Functions.ps1 '.\initial_release.sslbundle' -Module '*popup_refuel_ui_controller_new' -Function 'Refuel' -OutputPath 'D:\ModWork\functions'
.EXAMPLE
.\Export-Functions.ps1 'D:\ModWork\bundle-extracted'
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0, ValueFromRemainingArguments=$true)][string[]]$InputPath,
    [string]$OutputPath,
    [string]$Module = '*',
    [string]$Function = '*',
    [switch]$List,
    [switch]$Help
)
. "$PSScriptRoot\_Common.ps1"
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
try {
    $paths = @(Get-SnowPaths $InputPath)
    $files = @(foreach ($path in $paths) {
        if ([IO.Directory]::Exists($path)) {
            Get-SnowFolderFiles $path | Where-Object { $_.Extension -in @('.pak', '.zip', '.cache_block', '.sslbundle', '.sslb') } | ForEach-Object { $_.FullName }
        } else { $path }
    })
    $destination = $null
    if (-not $List) {
        $destination = Get-SnowWorkPath 'functions' $OutputPath
        if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
        [void][IO.Directory]::CreateDirectory($destination)
        Assert-SnowNotLinked $destination
    }
    $index = [Collections.Generic.List[object]]::new()
    $functionCount = 0
    foreach ($file in $files) {
        Assert-SnowNotLinked $file
        $moduleName = [IO.Path]::GetFileNameWithoutExtension($file)
        # Recover the script's actual name when reading a module extracted from a bundle.
        $parent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($file))
        for ($level = 0; $null -ne $parent -and $level -lt 3; $level++) {
            $manifest = Join-Path $parent.FullName '_snowpatcher/manifest.json'
            if ([IO.File]::Exists($manifest)) {
                Assert-SnowNotLinked $manifest
                $record = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
                if ($record.Kind -eq 'Bundle') {
                    $relative = $file.Substring($parent.FullName.Length + 1).Replace('\', '/')
                    $match = @($record.Entries | Where-Object { $_.Path -ceq $relative })
                    if ($match.Count -eq 1) { $moduleName = $match[0].Name }
                }
                break
            }
            $parent = $parent.Parent
        }
        foreach ($moduleData in (Get-SnowModules ([IO.File]::ReadAllBytes($file)) $moduleName $Module)) {
            $report = Read-SnowModule $moduleData.Data $moduleData.Name $Function
            if ($report.Functions.Count -eq 0) { continue }
            $functionCount += $report.Functions.Count
            if ($List) {
                foreach ($method in $report.Functions) {
                    [pscustomobject]@{ Module=$report.Module; Class=$method.Class; Function=$method.Name; Index=$method.Index; Instructions=$method.Instructions.Count }
                }
            } else {
                $leaf = ($report.Module.Split('.')[-1] -replace '[^a-zA-Z0-9_-]', '_')
                if ($leaf.Length -gt 60) { $leaf = $leaf.Substring(0,60) }
                $stem = '{0:D5}-{1}' -f $index.Count, $leaf
                Write-SnowJson (Join-Path $destination ($stem + '.json')) $report
                $text = [Text.StringBuilder]::new()
                [void]$text.AppendLine($report.Module)
                [void]$text.AppendLine('Readable names are followed by the original opcode and operand. Unknown operations keep op_NUMBER.')
                [void]$text.AppendLine('Member operands are dispatch slots, not table indices. Candidate names do not establish the receiver type.')
                [void]$text.AppendLine('Classes list their own type symbols separately from their base-type symbol. These are not interchangeable.')
                foreach ($class in $report.Classes) {
                    [void]$text.AppendLine(('  {0}: class {1}, type symbols [{2}], base-type symbol {3}' -f $class.Name, $class.Index, ($class.TypeSymbols -join ', '), $class.BaseTypeSymbol))
                }
                $assembly = [Text.StringBuilder]::new()
                [void]$assembly.AppendLine('; ' + $report.Module)
                foreach ($method in $report.Functions) {
                    [void]$text.AppendLine(); [void]$text.AppendLine(($method.Class + '::' + $method.Name + ' [function ' + $method.Index + ']'))
                    [void]$text.AppendLine(('  function type {0}; return type {1}; parameter types [{2}]; parameter names [{3}]' -f $method.FunctionTypeSymbol, $method.ReturnTypeSymbol, ($method.ParameterTypeSymbols -join ', '), ($method.ParameterNames -join ', ')))
                    [void]$text.AppendLine(('  signature: {0} ({1})' -f $method.ReturnTypeName, ($method.ParameterTypeNames -join ', ')))
                    foreach ($constant in $method.Constants) {
                        [void]$text.AppendLine(('  constant:{0} = {1} {2} [constant index {3}]' -f $constant.Name, $constant.Type, (ConvertTo-Json -InputObject $constant.Value -Compress -Depth 4), $constant.Index))
                    }
                    foreach ($instruction in $method.Instructions) {
                        [void]$text.AppendLine(('{0,5}: {1}  [opcode {2}, operand {3}] {4}' -f $instruction.Index, $instruction.Assembly, $instruction.Opcode, $instruction.Operand, $instruction.Description))
                    }
                    [void]$assembly.AppendLine(); [void]$assembly.AppendLine('; ' + $method.Class + '::' + $method.Name)
                    foreach ($line in $method.Assembly) { [void]$assembly.AppendLine($line) }
                }
                [IO.File]::WriteAllText((Join-Path $destination ($stem + '.txt')), $text.ToString(), $script:SnowUtf8)
                [IO.File]::WriteAllText((Join-Path $destination ($stem + '.asm')), $assembly.ToString(), $script:SnowUtf8)
                $index.Add([pscustomobject]@{ Source=$file; Module=$report.Module; Functions=$report.Functions.Count; Json=($stem + '.json'); Text=($stem + '.txt'); Assembly=($stem + '.asm') })
                Write-Host ("Exported {0} ({1} functions)" -f $report.Module, $report.Functions.Count)
            }
        }
    }
    if ($functionCount -eq 0) { throw 'No matching compiled functions found. Check the input and the -Module/-Function filters.' }
    if (-not $List) {
        Write-SnowJson (Join-Path $destination 'index.json') $index.ToArray()
        Write-Host ("Exported {0} functions to:" -f $functionCount)
        Write-Output $destination
    }
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
