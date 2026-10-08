Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$rm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'RenderModify' }
$cctor = $rm.Methods | Where-Object { $_.Name -eq '.cctor' }
if ($cctor) {
    foreach ($inst in $cctor.Body.Instructions) {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
    }
}
