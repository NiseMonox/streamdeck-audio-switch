<#
  Builds the "音频切换" Stream Deck plugin with the C# compiler that ships with Windows.

    .\build.ps1            compile AudioSwitch.exe
    .\build.ps1 -Package   compile and create dist\<uuid>.streamDeckPlugin (double-click to install)
    .\build.ps1 -Install   compile, copy into Stream Deck's plugin folder and restart Stream Deck
#>
param([switch]$Install, [switch]$Package)
$ErrorActionPreference = 'Stop'

$uuid   = 'com.nisemonox.audioswitch'
$plugin = Join-Path $PSScriptRoot "$uuid.sdPlugin"
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    /out:"$plugin\AudioSwitch.exe" /reference:System.Web.Extensions.dll /reference:System.Management.dll `
    "$PSScriptRoot\src\AudioSwitch.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
"Built $plugin\AudioSwitch.exe"

if ($Package) {
    # A .streamDeckPlugin is a zip with the .sdPlugin folder at its root.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $dist = Join-Path $PSScriptRoot 'dist'
    $out  = Join-Path $dist "$uuid.streamDeckPlugin"
    New-Item -ItemType Directory -Force $dist | Out-Null
    if (Test-Path $out) { Remove-Item $out }
    $zip = [IO.Compression.ZipFile]::Open($out, 'Create')
    try {
        foreach ($file in Get-ChildItem $plugin -Recurse -File | Where-Object Extension -ne '.log') {
            $entry = "$uuid.sdPlugin/" + $file.FullName.Substring($plugin.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal')
        }
    }
    finally { $zip.Dispose() }
    "Packaged $out"
}

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
