$log = 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\BepInEx\LogOutput.log'
if (Test-Path $log) {
    Get-Content $log | Select-String -Pattern 'MaterialDiag|Skinned mesh|using material|shader=' | Select-Object -Last 30
} else {
    Write-Host "Log not found"
}
