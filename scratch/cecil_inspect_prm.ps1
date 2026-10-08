Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$prm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'PlayerRaceModel' }
Write-Host "PlayerRaceModel Methods:"
foreach ($m in $prm.Methods) {
    Write-Host "  Method: $($m.Name)"
}
Write-Host "PlayerRaceModel Fields:"
foreach ($f in $prm.Fields) {
    Write-Host "  Field: $($f.Name) : $($f.FieldType.Name)"
}
