Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
$awake = $ccm.Methods | Where-Object { $_.Name -eq 'Awake' -or $_.Name -eq 'Start' -or $_.Name -eq 'Init' }
foreach ($m in $awake) {
    Write-Host "=== CCM::$($m.Name) ==="
    foreach ($inst in $m.Body.Instructions) {
        if ($inst.Operand -and $inst.Operand.ToString() -match 'Slider|minValue|maxValue|Hue|Brightness') {
            Write-Host "$($inst.OpCode) $($inst.Operand)"
        }
    }
}
