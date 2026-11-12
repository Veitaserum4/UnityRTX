Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
foreach ($m in $ccm.Methods) {
    if ($m.HasBody) {
        foreach ($inst in $m.Body.Instructions) {
            if ($inst.Operand -and $inst.Operand.ToString() -match '_hueSlider|_skinColor|_setSkinColorProfile') {
                Write-Host "$($m.Name) -> $($inst.Operand)"
            }
        }
    }
}
