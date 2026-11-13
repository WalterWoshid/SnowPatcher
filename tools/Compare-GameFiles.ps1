<#
.SYNOPSIS
Report changes between two containers, modules or folders. Does not merge them.
.EXAMPLE
.\Compare-GameFiles.ps1 '.\original\initial.pak' '.\modified\initial.pak'
.EXAMPLE
.\Compare-GameFiles.ps1 '.\original' '.\modified' -OutputPath 'D:\ModWork\comparison'
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0)][string]$Original,
    [Parameter(Position=1)][string]$Modified,
    [string]$OutputPath,
    [switch]$Help
)
. "$PSScriptRoot\_Common.ps1"
if ($Help) { Get-Help $PSCommandPath -Detailed; return }
try {
    if (-not $Original -or -not $Modified) { throw 'Supply an original and a modified path. Use -Help for examples.' }
    $left = (Get-Item -LiteralPath $Original).FullName
    $right = (Get-Item -LiteralPath $Modified).FullName
    Assert-SnowNotLinked $left; Assert-SnowNotLinked $right
    $changes = [Collections.Generic.List[object]]::new()
    if ([IO.Directory]::Exists($left) -and [IO.Directory]::Exists($right)) {
        $leftMap = @{}; $rightMap = @{}
        foreach ($file in (Get-SnowFolderFiles $left)) { $leftMap[$file.FullName.Substring($left.TrimEnd('\','/').Length + 1).Replace('\','/')] = $file.FullName }
        foreach ($file in (Get-SnowFolderFiles $right)) { $rightMap[$file.FullName.Substring($right.TrimEnd('\','/').Length + 1).Replace('\','/')] = $file.FullName }
        foreach ($key in @(@($leftMap.Keys) + @($rightMap.Keys) | Sort-Object -Unique)) {
            if (-not $leftMap.ContainsKey($key)) { $changes.Add([pscustomobject]@{ Change='Added'; Path=$key }) }
            elseif (-not $rightMap.ContainsKey($key)) { $changes.Add([pscustomobject]@{ Change='Removed'; Path=$key }) }
            else { foreach ($change in (Compare-SnowData ([IO.File]::ReadAllBytes($leftMap[$key])) ([IO.File]::ReadAllBytes($rightMap[$key])) $key)) { $changes.Add($change) } }
        }
    } elseif ([IO.File]::Exists($left) -and [IO.File]::Exists($right)) {
        foreach ($change in (Compare-SnowData ([IO.File]::ReadAllBytes($left)) ([IO.File]::ReadAllBytes($right)) ([IO.Path]::GetFileName($left)))) { $changes.Add($change) }
    } else { throw 'Compare two files or two folders.' }
    $destination = Get-SnowWorkPath 'comparison' $OutputPath
    if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
    [void][IO.Directory]::CreateDirectory($destination)
    Assert-SnowNotLinked $destination
    Write-SnowJson (Join-Path $destination 'changes.json') ([ordered]@{ Original=$left; Modified=$right; Changes=$changes.ToArray() })
    $text = [Collections.Generic.List[string]]::new()
    $text.Add("Original: $left"); $text.Add("Modified: $right"); $text.Add('')
    if ($changes.Count -eq 0) { $text.Add('No changes.') }
    foreach ($change in $changes) { $text.Add($change.Change + ': ' + $change.Path) }
    [IO.File]::WriteAllLines((Join-Path $destination 'changes.txt'), $text.ToArray(), $script:SnowUtf8)
    Write-Host ("{0} change records. Report:" -f $changes.Count)
    Write-Output $destination
} catch { Write-Error $_.Exception.Message -ErrorAction Continue; exit 1 }
