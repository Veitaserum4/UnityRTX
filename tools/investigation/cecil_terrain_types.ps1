Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

Write-Host "Types related to Map / Terrain / World / Chunk / Zone / Dungeon:"
foreach ($type in $assembly.MainModule.Types) {
    if ($type.Name -match 'Terrain|Map|Zone|Dungeon|Chunk|Environment|Ground|World') {
        Write-Host "Type: $($type.FullName)"
    }
}
