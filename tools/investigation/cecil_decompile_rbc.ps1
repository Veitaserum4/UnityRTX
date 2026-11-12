Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
Write-Host "=== CharacterCreationManager instructions in Randomize_BodyColor ==="
$m = $ccm.Methods | Where-Object { $_.Name -eq 'Randomize_BodyColor' }
if ($m) {
    foreach ($inst in $m.Body.Instructions) {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
    }
}
