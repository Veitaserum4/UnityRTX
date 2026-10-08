Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
$m = $ccm.Methods | Where-Object { $_.Name -match 'Init_SliderParams' }
if ($m) {
    foreach ($inst in $m.Body.Instructions) {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
    }
}
