param(
    [Parameter(Mandatory)][string]$Config,
    [string]$SignTool,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Config = (Resolve-Path -LiteralPath $Config).Path
if ([bool]$SignTool -ne [bool]$CertificateThumbprint) { throw 'Supply both SignTool and CertificateThumbprint.' }
if (!(Test-Path -LiteralPath (Join-Path $repo 'Player/ffmpeg/manifest.json'))) { throw 'Run Install-FFmpeg.ps1 first.' }
Push-Location $repo
try {
    $staging = Join-Path $repo ('artifacts/publish-' + [Guid]::NewGuid().ToString('N'))
    $player = Join-Path $staging 'player'
    $obfuscated = Join-Path $staging 'obfuscated'
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Tool restore failed.' }
    dotnet publish Player/Player.csproj -c Release -o $player "-p:PlayerConfigFile=$Config" -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Player publish failed.' }
    $configXml = @"
<Obfuscator>
  <Var name="InPath" value="$([Security.SecurityElement]::Escape($player))" />
  <Var name="OutPath" value="$([Security.SecurityElement]::Escape($obfuscated))" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Module file="$([Security.SecurityElement]::Escape((Join-Path $player 'Player.dll')))">
    <SkipType name="SecureVideo.Player.App" skipMethods="true" skipFields="true" skipProperties="true" skipEvents="true" />
    <SkipType name="SecureVideo.Player.MainWindow" skipMethods="true" skipFields="true" skipProperties="true" skipEvents="true" />
    <SkipType name="SecureVideo.Player.PlayerConfig" skipMethods="true" skipFields="true" skipProperties="true" skipEvents="true" />
    <SkipType name="*CachedLogin" skipMethods="true" skipFields="true" skipProperties="true" skipEvents="true" />
  </Module>
</Obfuscator>
"@
    $xmlPath = Join-Path $staging 'obfuscar.xml'
    [IO.File]::WriteAllText($xmlPath, $configXml, [Text.UTF8Encoding]::new($false))
    dotnet tool run obfuscar.console $xmlPath
    if ($LASTEXITCODE) { throw 'Player obfuscation failed.' }
    Copy-Item -LiteralPath (Join-Path $obfuscated 'Player.dll') -Destination $player -Force
    if ($SignTool) {
        foreach ($name in 'Player.exe', 'Player.dll', 'Core.dll') {
            $binary = Join-Path $player $name
            & $SignTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $binary
            if ($LASTEXITCODE) { throw "Signing failed for $name." }
            & $SignTool verify /pa $binary
            if ($LASTEXITCODE) { throw "Signature verification failed for $name." }
        }
    }
    Write-Output "Published player: $player"
}
finally { Pop-Location }
