Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

foreach ($type in $assembly.MainModule.Types) {
    if ($type.Name -match 'ColorAdjust') {
        Write-Host "Type: $($type.FullName)"
    }
    foreach ($m in $type.Methods) {
        if ($m.HasBody) {
            foreach ($inst in $m.Body.Instructions) {
                if ($inst.OpCode.ToString() -eq 'ldstr' -and $inst.Operand -match 'ColorAdjust') {
                    Write-Host "$($type.Name)::$($m.Name) -> $($inst.Operand)"
                }
            }
        }
    }
}
