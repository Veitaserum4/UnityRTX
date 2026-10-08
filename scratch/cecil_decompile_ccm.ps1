Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$ccm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterCreationManager' }
Write-Host "=== CharacterCreationManager Methods ==="
foreach ($m in $ccm.Methods) {
    if ($m.Name -match 'Color|Hue|Tint|Profile') {
        Write-Host "  Method: $($m.Name)"
    }
}
