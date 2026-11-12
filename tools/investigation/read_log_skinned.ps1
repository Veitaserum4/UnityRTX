$log = 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\LogOutput.log'
if (Test-Path $log) {
    Get-Content $log | Select-String -Pattern 'Scene loaded|Skinned mesh|Material' | Select-Object -Last 40
}
