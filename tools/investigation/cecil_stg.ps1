Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$stg = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'SkinTextureGroup' }
Write-Host "SkinTextureGroup Fields:"
foreach ($f in $stg.Fields) {
    Write-Host "  $($f.Name) : $($f.FieldType.FullName)"
}
