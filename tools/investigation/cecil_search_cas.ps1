Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

foreach ($type in $assembly.MainModule.Types) {
    foreach ($m in $type.Methods) {
        if ($m.Name -match 'ColorAdjustShader') {
            Write-Host "$($type.FullName)::$($m.Name)"
        }
    }
}
