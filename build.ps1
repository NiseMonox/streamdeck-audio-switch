<#
  Builds the "音频切换" Stream Deck plugin with the C# compiler that ships with Windows.

    .\build.ps1            compile AudioSwitch.exe
    .\build.ps1 -Install   compile, copy into Stream Deck's plugin folder and restart Stream Deck
#>
param([switch]$Install)
$ErrorActionPreference = 'Stop'

$uuid   = 'com.nisemonox.audioswitch'
$plugin = Join-Path $PSScriptRoot "$uuid.sdPlugin"
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    /out:"$plugin\AudioSwitch.exe" /reference:System.Web.Extensions.dll /reference:System.Management.dll `
    "$PSScriptRoot\src\AudioSwitch.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
"Built $plugin\AudioSwitch.exe"

if (-not $Install) { return }

$target = Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins\$uuid.sdPlugin"
$app = Get-Process StreamDeck -ErrorAction SilentlyContinue | Select-Object -First 1
$appPath = if ($app) { $app.Path }

# Stream Deck only loads new plugins at startup, and a running plugin's exe cannot be replaced.
if ($app) {
    Stop-Process -Id $app.Id
    $app.WaitForExit(10000) | Out-Null
}
Get-Process AudioSwitch -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target\*" } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Mirror, so files removed from the plugin disappear from the installed copy too.
robocopy $plugin $target /MIR /XF *.log /NJH /NJS /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copy failed (robocopy exit code $LASTEXITCODE)." }
"Installed to $target"

if ($appPath) {
    Start-Process $appPath
    'Restarted Stream Deck'
}
