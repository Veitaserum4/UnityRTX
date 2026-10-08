Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$tm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'TerrainManager' }
if ($tm) {
    Write-Host "=== TerrainManager Methods ==="
    foreach ($m in $tm.Methods) { Write-Host "  $($m.Name)" }
    Write-Host "=== TerrainManager Fields ==="
    foreach ($f in $tm.Fields) { Write-Host "  $($f.Name) : $($f.FieldType.FullName)" }
}

$tmd = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'TerrainManagerData' }
if ($tmd) {
    Write-Host "=== TerrainManagerData Methods ==="
    foreach ($m in $tmd.Methods) { Write-Host "  $($m.Name)" }
    Write-Host "=== TerrainManagerData Fields ==="
    foreach ($f in $tmd.Fields) { Write-Host "  $($f.Name) : $($f.FieldType.FullName)" }
}
