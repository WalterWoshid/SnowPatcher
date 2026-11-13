<#
.SYNOPSIS
Look up readable instructions and script references, including their numeric values.
.EXAMPLE
.\Resolve-ScriptNames.ps1 -Instruction call
.EXAMPLE
.\Resolve-ScriptNames.ps1 -Instruction 13
.EXAMPLE
.\Resolve-ScriptNames.ps1 '.\functions\00000-main_menu_navigation_panel.json' -Reference 'member:*::SetText'
.EXAMPLE
.\Resolve-ScriptNames.ps1 '.\module.sslb' -Kind member -Slot 30
.EXAMPLE
.\Resolve-ScriptNames.ps1 '.\functions\00000-main_menu_navigation_panel.json' -Kind constant -Function SetBtns
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0)][string]$InputPath,
    [string]$Instruction,
    [string]$Reference = '*',
    [ValidateSet('member','function','type','global','string','constant')][string]$Kind,
    [Nullable[int]]$Index,
    [Nullable[int]]$Slot,
    [string]$Module = '*',
    [string]$Function = '*',
    [switch]$Help
)
. "$PSScriptRoot\_Common.ps1"
. "$PSScriptRoot\_Authoring.ps1"
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
try {
    $count = 0
    if ($Instruction -or -not $InputPath) {
        $pattern = if ($Instruction) { $Instruction } else { '*' }
        foreach ($opcode in @( (Get-SnowOpcodes).Values | Sort-Object Opcode )) {
            if ($opcode.Name -like $pattern -or [string]$opcode.Opcode -eq $pattern) {
                $count++; [pscustomobject]@{ Record='Instruction'; Name=$opcode.Name; Opcode=$opcode.Opcode; Operand=$opcode.Operand; EstablishedName=$opcode.Known }
            }
        }
    }
    if ($InputPath) {
        $path = (Get-Item -LiteralPath $InputPath).FullName
        Assert-SnowNotLinked $path
        $reports = @()
        if ([IO.Path]::GetExtension($path) -ieq '.json') {
            $report = Read-SnowAuthorJson ([IO.File]::ReadAllText($path)) -AsObject
            if (-not $report.PSObject.Properties['ModuleFields']) { throw 'Input JSON must be a module export from Export-Functions.ps1.' }
            if (-not $report.PSObject.Properties['References']) { Add-SnowReadableNames $report }
            if ($report.Module -like $Module) { $reports = @($report) }
        } else {
            $reports = @(foreach ($data in (Get-SnowModules ([IO.File]::ReadAllBytes($path)) ([IO.Path]::GetFileNameWithoutExtension($path)) $Module)) {
                Read-SnowModule $data.Data $data.Name $Function
            })
        }
        foreach ($report in $reports) {
            foreach ($row in $report.References) {
                if (($Kind -and $row.Kind -ne $Kind) -or ($row.Name -notlike $Reference -and $row.Reference -notlike $Reference)) { continue }
                if ($null -ne $Index -and $row.Index -ne $Index) { continue }
                $memberSlot = if ($row.PSObject.Properties['Slot']) { $row.Slot } else { $null }
                if ($null -ne $Slot -and $memberSlot -ne $Slot) { continue }
                $count++; [pscustomobject]@{ Record='Reference'; Module=$report.Module; Kind=$row.Kind; Reference=$row.Reference; Table=$row.Table; Index=$row.Index; Slot=$memberSlot }
            }
            if (-not $Kind -or $Kind -eq 'constant') {
                foreach ($method in $report.Functions) {
                    if ($method.Name -notlike $Function -and ($method.Class + '::' + $method.Name) -notlike $Function) { continue }
                    foreach ($constant in $method.Constants) {
                        $name = 'constant:' + $constant.Name
                        if (($name -notlike $Reference -and $constant.Name -notlike $Reference) -or ($null -ne $Index -and $constant.Index -ne $Index) -or $null -ne $Slot) { continue }
                        $count++; [pscustomobject]@{ Record='Reference'; Module=$report.Module; Function=($method.Class + '::' + $method.Name); Kind='constant'; Reference=$name; Table='function.constants'; Index=$constant.Index; Type=$constant.Type; Value=$constant.Value }
                    }
                }
            }
        }
    }
    if (-not $count) { throw 'No matching instruction or reference found.' }
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
