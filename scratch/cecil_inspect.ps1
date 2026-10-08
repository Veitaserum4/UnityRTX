Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

Write-Host "Types in Assembly-CSharp:"
foreach ($type in $assembly.MainModule.Types) {
    if ($type.Name -match 'Visual|Custom|Player|Color|Cloth|Skin|Fur|Character') {
        Write-Host "Type: $($type.FullName)"
    }
}
