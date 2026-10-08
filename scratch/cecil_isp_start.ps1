Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
$m = $ccm.Methods | Where-Object { $_.Name -match 'Init_SliderParams' }
for ($i = 0; $i -lt 50; $i++) {
    $inst = $m.Body.Instructions[$i]
    Write-Host "$($inst.OpCode) $($inst.Operand)"
}
