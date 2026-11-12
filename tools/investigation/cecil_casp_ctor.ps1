Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$casp = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'ColorAdjustShader_Profile' }
$ctor = $casp.Methods | Where-Object { $_.Name -eq '.ctor' }
foreach ($inst in $ctor.Body.Instructions) {
    Write-Host "$($inst.OpCode) $($inst.Operand)"
}
