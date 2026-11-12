Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$prm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'PlayerRaceModel' }

Write-Host "=== .cctor ==="
$cctor = $prm.Methods | Where-Object { $_.Name -eq '.cctor' }
if ($cctor) {
    foreach ($inst in $cctor.Body.Instructions) {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
    }
}

Write-Host "=== Awake ==="
$awake = $prm.Methods | Where-Object { $_.Name -eq 'Awake' }
if ($awake) {
    foreach ($inst in $awake.Body.Instructions) {
        if ($inst.OpCode.ToString() -match 'ldstr|call|stfld|ldfld') {
            Write-Host "$($inst.OpCode) $($inst.Operand)"
        }
    }
}
