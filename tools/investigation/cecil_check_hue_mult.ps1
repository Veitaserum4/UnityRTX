Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$prm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'PlayerRaceModel' }
$m = $prm.Methods | Where-Object { $_.Name -eq 'Apply_CharacterDisplay' }
# Find all instructions setting _HUE
for ($i = 0; $i -lt $m.Body.Instructions.Count; $i++) {
    $inst = $m.Body.Instructions[$i]
    if ($inst.Operand -and $inst.Operand.ToString() -match '_HUE') {
        Write-Host "Instruction $i sets _HUE. Preceding instructions:"
        for ($j = [Math]::Max(0, $i - 8); $j -le $i + 2; $j++) {
            Write-Host "  $($m.Body.Instructions[$j].OpCode) $($m.Body.Instructions[$j].Operand)"
        }
    }
}
