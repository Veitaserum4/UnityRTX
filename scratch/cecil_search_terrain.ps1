Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

Write-Host "Searching for Terrain references in Assembly-CSharp:"
foreach ($type in $assembly.MainModule.Types) {
    foreach ($m in $type.Methods) {
        if ($m.HasBody) {
            foreach ($inst in $m.Body.Instructions) {
                if ($inst.Operand -and $inst.Operand.ToString() -match 'UnityEngine.Terrain') {
                    Write-Host "$($type.Name)::$($m.Name) -> $($inst.Operand)"
                }
            }
        }
    }
}
