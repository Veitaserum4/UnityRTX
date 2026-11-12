Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
$m = $ccm.Methods | Where-Object { $_.Name -eq 'Handle_CharacterCreationDisplay' }
$print = $false
foreach ($inst in $m.Body.Instructions) {
    if ($inst.Operand -and $inst.Operand.ToString() -match '_setSkinColorProfile') {
        $print = $true
    }
    if ($print) {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
        if ($inst.OpCode.ToString() -match 'call') {
            # continue a few lines then break
        }
    }
}
