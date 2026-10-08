Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$rmed = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'RaceModelEquipDisplay' }
if ($rmed) {
    Write-Host "=== RaceModelEquipDisplay Methods ==="
    foreach ($m in $rmed.Methods) {
        Write-Host "  Method: $($m.Name)"
    }
    Write-Host "=== RaceModelEquipDisplay Fields ==="
    foreach ($f in $rmed.Fields) {
        Write-Host "  Field: $($f.Name) : $($f.FieldType.Name)"
    }
}
