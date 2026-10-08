Add-Type -Path 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\core\Mono.Cecil.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly('C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed\Assembly-CSharp.dll')

$t = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'CharacterParamsGroup' }
Write-Host "CharacterParamsGroup Fields:"
foreach ($f in $t.Fields) {
    Write-Host "  $($f.Name) : $($f.FieldType.FullName)"
}

$spr = $assembly.MainModule.Types | Where-Object { $_.Name -eq 'ScriptablePlayerRace' }
Write-Host "ScriptablePlayerRace Fields:"
foreach ($f in $spr.Fields) {
    Write-Host "  $($f.Name) : $($f.FieldType.FullName)"
}
