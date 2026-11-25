param([string]$Source, [switch]$ServeOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $demo = Join-Path $repo 'artifacts/local-demo'
    $env:SVIDEO_VAULT = Join-Path $demo 'vault'
    $env:SVIDEO_ALLOW_LOCAL_HTTP = '1'
    $server = Join-Path $repo 'Server/bin/Release/net8.0-windows/Server.exe'
    $player = Join-Path $repo 'Player/bin/Release/net8.0-windows/Player.exe'
    if ($ServeOnly) {
        if (!(Test-Path -LiteralPath (Join-Path $env:SVIDEO_VAULT 'vault.dat'))) { throw 'Initialize the demo first.' }
        & $server serve
        exit $LASTEXITCODE
    }

    if (!(Test-Path -LiteralPath 'Player/ffmpeg/manifest.json')) { & (Join-Path $PSScriptRoot 'Install-FFmpeg.ps1') }
    dotnet build SecureVideo.sln -c Release
    if ($LASTEXITCODE) { throw 'Build failed.' }

    function Invoke-Admin {
        & $server @args
        if ($LASTEXITCODE) { throw 'Administration command failed.' }
    }

    Invoke-Admin init
    $marker = Join-Path $demo 'account-created'
    if (!(Test-Path -LiteralPath $marker)) {
        Write-Host 'Set the password for local account demo (3–256 characters).'
        Invoke-Admin add-user demo 30 1
        New-Item -ItemType File -Path $marker | Out-Null
    }

    $ownerMarker = Join-Path $demo 'owner-created'
    if (!(Test-Path -LiteralPath $ownerMarker)) {
        $demoOwnerPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        $demoOwnerPassword | & $server add-admin demo-owner 3650 1
        if ($LASTEXITCODE) { throw 'Demo video owner creation failed.' }
        New-Item -ItemType File -Path $ownerMarker | Out-Null
    }

    if (!$Source) {
        $Source = Join-Path $demo 'sample.mp4'
        $ffmpeg = Join-Path $repo 'artifacts/ffmpeg-tools/ffmpeg.exe'
        if (!(Test-Path -LiteralPath $Source)) {
            & $ffmpeg -v error -f lavfi -i 'testsrc2=size=640x360:rate=24' -f lavfi -i 'sine=frequency=440' -t 12 -c:v mpeg4 -q:v 5 -c:a aac $Source
            if ($LASTEXITCODE) { throw 'Sample generation failed.' }
        }
    }
    $Source = (Resolve-Path -LiteralPath $Source).Path
    $encrypted = Join-Path $demo ('video-' + [Guid]::NewGuid().ToString('N') + '.svideo')
    $id = Invoke-Admin import demo-owner $Source $encrypted
    Invoke-Admin grant demo $id

    $config = Join-Path $demo ('player-' + [Guid]::NewGuid().ToString('N') + '.json')
    Invoke-Admin export-config 'http://127.0.0.1:5185' $config
    Copy-Item -LiteralPath $config -Destination (Join-Path (Split-Path -Parent $player) 'player.json') -Force

    $service = Start-Process -FilePath $server -ArgumentList 'serve' -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Milliseconds 900
        if ($service.HasExited) { throw 'Authorization service failed to start. Check whether port 5185 is occupied.' }
        Write-Host "Sign in as demo and open $encrypted"
        Start-Process -FilePath $player -Wait
    }
    finally {
        if (!$service.HasExited) { Stop-Process -Id $service.Id }
        $service.Dispose()
    }
}
finally { Pop-Location }
