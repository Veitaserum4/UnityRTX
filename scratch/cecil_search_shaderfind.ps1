Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

Write-Host "Searching for Shader.Find:"
foreach ($type in $assembly.MainModule.Types) {
    foreach ($m in $type.Methods) {
        if ($m.HasBody) {
            foreach ($inst in $m.Body.Instructions) {
                if ($inst.Operand -and $inst.Operand.ToString() -match 'Shader::Find') {
                    Write-Host "$($type.Name)::$($m.Name) calls Shader.Find"
                    # let's look at previous instructions to find ldstr
                    $idx = $m.Body.Instructions.IndexOf($inst)
                    for ($i = [Math]::Max(0, $idx - 5); $i -le $idx; $i++) {
                        Write-Host "  $($m.Body.Instructions[$i].OpCode) $($m.Body.Instructions[$i].Operand)"
                    }
                }
            }
        }
    }
}
