Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$rmed = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'RaceModelEquipDisplay' }
$smcp = $rmed.Methods | Where-Object { $_.Name -eq 'Set_MaterialColorProfile' }
Write-Host "=== Set_MaterialColorProfile Instructions ==="
foreach ($inst in $smcp.Body.Instructions) {
    Write-Host "$($inst.OpCode) $($inst.Operand)"
}
