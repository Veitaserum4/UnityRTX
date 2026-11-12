Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$casp = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'ColorAdjustShader_Profile' }
Write-Host "=== ColorAdjustShader_Profile Methods ==="
foreach ($m in $casp.Methods) {
    Write-Host "  Method: $($m.Name)"
}
Write-Host "=== ColorAdjustShader_Profile Fields ==="
foreach ($f in $casp.Fields) {
    Write-Host "  Field: $($f.Name) : $($f.FieldType.Name)"
}

$prm = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'PlayerRaceModel' }
$acd = $prm.Methods | Where-Object { $_.Name -eq 'Apply_CharacterDisplay' }
Write-Host "=== Apply_CharacterDisplay calls/strings ==="
foreach ($inst in $acd.Body.Instructions) {
    if ($inst.OpCode.ToString() -match 'call|ldstr|stsfld|ldsfld') {
        Write-Host "$($inst.OpCode) $($inst.Operand)"
    }
}
