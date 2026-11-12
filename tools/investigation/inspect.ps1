$managed = 'C:\Program Files (x86)\Steam\steamapps\common\ATLYSS\ATLYSS_Data\Managed'
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $args)
    $name = ($args.Name -split ',')[0] + '.dll'
    $path = Join-Path $managed $name
    if (Test-Path $path) {
        return [System.Reflection.Assembly]::LoadFrom($path)
    }
    return $null
})

try {
    $asmPath = Join-Path $managed 'Assembly-CSharp.dll'
    $asm = [System.Reflection.Assembly]::LoadFrom($asmPath)
    Write-Host "Loaded successfully! Types count: $($asm.GetTypes().Length)"
    
    foreach ($t in $asm.GetTypes()) {
        if ($t.Name -match 'Player.*Visual|Character.*Custom|Player.*Skin|Player.*Color|Player.*Cloth|Player.*Mesh|Player.*Model') {
            Write-Host "Found Type: $($t.FullName)"
        }
    }
} catch {
    Write-Host "Error: $_"
    if ($_.Exception.LoaderExceptions) {
        foreach ($e in $_.Exception.LoaderExceptions) {
            Write-Host "  LoaderException: $e"
        }
    }
}
