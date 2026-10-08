Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$rm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'RenderModify' }
$method = $rm.Methods | Where-Object { $_.Name -eq 'Apply_ColorAdjustShaderParams' }
Write-Host "=== RenderModify::Apply_ColorAdjustShaderParams ==="
foreach ($inst in $method.Body.Instructions) {
    Write-Host "$($inst.OpCode) $($inst.Operand)"
}
