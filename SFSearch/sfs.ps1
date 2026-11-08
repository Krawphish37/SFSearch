#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$projDir = $PSScriptRoot
$exe = Join-Path $projDir 'bin\Release\net10.0\SFSearch.exe'
$dll = Join-Path $projDir 'bin\Release\net10.0\SFSearch.dll'

$needsBuild = -not (Test-Path $exe) -or -not (Test-Path $dll)
if (-not $needsBuild) {
    $dllTime = (Get-Item $dll).LastWriteTimeUtc
    # Every source file, subfolders included (Service\...), but not build output
    $sources = @(Get-ChildItem -Path $projDir -Filter '*.cs' -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    $csproj  = Get-Item (Join-Path $projDir 'SFSearch.csproj')
    foreach ($s in ($sources + $csproj)) {
        if ($s.LastWriteTimeUtc -gt $dllTime) {
            $needsBuild = $true
            break
        }
    }
}

if ($needsBuild) {
    Write-Host '[sfs] Building SFSearch (Release)...' -ForegroundColor DarkGray
    $buildOut = & dotnet build (Join-Path $projDir 'SFSearch.csproj') -c Release --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildOut | Write-Error
        exit $LASTEXITCODE
    }
}

# Extensions built from source on this PC (extensions\<name>.project holds the project's path, written by its build):
# rebuild one when its code changed or SFSearch was rebuilt. A .dll dropped in with no .project is used as it is.
$extDir = Join-Path $projDir 'extensions'
if (Test-Path $extDir) {
    foreach ($marker in Get-ChildItem -Path $extDir -Filter '*.project' -File) {
        $extProj = (Get-Content $marker.FullName -Raw).Trim()
        if (-not (Test-Path $extProj)) { continue }
        $extDll = Join-Path $extDir ($marker.BaseName + '.dll')
        $stale = -not (Test-Path $extDll)
        if (-not $stale) {
            # The .project file is written on every build of the add-on; the .dll keeps its compile time when copied
            $extTime = $marker.LastWriteTimeUtc
            $stale = (Get-Item $dll).LastWriteTimeUtc -gt $extTime
            if (-not $stale) {
                $newer = Get-ChildItem -Path (Split-Path $extProj) -File -Recurse | Where-Object { ($_.Extension -eq '.cs' -or $_.Extension -eq '.csproj') -and $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $extTime } | Select-Object -First 1
                $stale = $null -ne $newer
            }
        }
        if ($stale) {
            Write-Host "[sfs] Building extension $($marker.BaseName)..." -ForegroundColor DarkGray
            $buildOut = & dotnet build $extProj -c Release --nologo -v q 2>&1
            if ($LASTEXITCODE -ne 0) { $buildOut | Write-Error; exit $LASTEXITCODE }
        }
    }
}

& $exe @args
exit $LASTEXITCODE
