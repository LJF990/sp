param([string]$Archive)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repo 'Player/ffmpeg'
$work = Join-Path ([IO.Path]::GetTempPath()) ('SecureVideo-ffmpeg-' + [Guid]::NewGuid().ToString('N'))
$expected = 'DBED3E64D68DE76472BF2CD0B0F3250384FB96BAC14B635A708E998F6E1D6996'
$url = 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-24-12-41/ffmpeg-n8.1.3-41-ga8029b6528-win64-lgpl-shared-8.1.zip'
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if (!$Archive) {
        $Archive = Join-Path $work 'ffmpeg.zip'
        curl.exe --retry 3 --retry-all-errors --connect-timeout 10 --max-time 120 --fail --location --silent --show-error $url -o $Archive
        if ($LASTEXITCODE) { throw 'FFmpeg download failed.' }
    }
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expected) {
        throw 'Unexpected FFmpeg archive SHA-256.'
    }
    $expanded = Join-Path $work 'expanded'
    Expand-Archive -LiteralPath $Archive -DestinationPath $expanded
    $distribution = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
    $dlls = @(Get-ChildItem -LiteralPath (Join-Path $distribution.FullName 'bin') -Filter '*.dll')
    if (!$dlls -or !($dlls.Name -contains 'avformat-62.dll')) { throw 'Unexpected FFmpeg archive contents.' }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $target -Filter '*.dll') {
        Remove-Item -LiteralPath $file.FullName
    }
    $manifest = [ordered]@{}
    foreach ($dll in $dlls) {
        Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
        $manifest[$dll.Name] = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
    }
    Get-ChildItem -LiteralPath $distribution.FullName -File |
        Where-Object Name -Match '^(LICENSE|COPYING|README)' |
        Copy-Item -Destination $target -Force
    [IO.File]::WriteAllText((Join-Path $target 'manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $tools = Join-Path $repo 'artifacts/ffmpeg-tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Copy-Item -Path (Join-Path $distribution.FullName 'bin/*') -Destination $tools -Force
    Write-Output "Installed FFmpeg 8.1 in $target"
}
finally {
    $resolved = (Resolve-Path -LiteralPath $work).Path
    $tempRoot = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $tempRoot -and
        [IO.Path]::GetFileName($resolved) -like 'SecureVideo-ffmpeg-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
