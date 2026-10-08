Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$pv = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'PlayerVisual' }
Write-Host "PlayerVisual Methods:"
foreach ($m in $pv.Methods) {
    Write-Host "  Method: $($m.Name)"
}
Write-Host "PlayerVisual Fields:"
foreach ($f in $pv.Fields) {
    if ($f.Name -match 'Color|Mat|Render|Block|Prop') {
        Write-Host "  Field: $($f.Name) : $($f.FieldType.Name)"
    }
}
