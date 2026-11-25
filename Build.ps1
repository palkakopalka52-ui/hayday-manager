$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Windows .NET Framework 4.x C# compiler is unavailable.' }
$references = @(
    'System.Drawing.dll','System.Management.dll','System.Runtime.Serialization.dll','System.Xaml.dll','WindowsBase.dll',
    'PresentationCore.dll','PresentationFramework.dll','System.Runtime.dll','System.Runtime.WindowsRuntime.dll',
    'System.Runtime.InteropServices.WindowsRuntime.dll'
)
$cscArgs = @('/nologo','/langversion:5','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/utf8output',
    "/out:$(Join-Path $PSScriptRoot 'HayDayManager.exe')","/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')")
foreach ($name in $references) {
    $file = Join-Path $framework $name
    if (-not (Test-Path -LiteralPath $file)) { $file = Join-Path (Join-Path $framework 'WPF') $name }
    if (-not (Test-Path -LiteralPath $file)) { throw "Framework assembly missing: $name" }
    $cscArgs += "/reference:$file"
}
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
foreach ($name in 'Foundation','Globalization','Graphics','Media','Storage') {
    $file = Join-Path $metadata "Windows.$name.winmd"
    if (-not (Test-Path -LiteralPath $file)) { throw "Windows Runtime metadata missing: $name" }
    $cscArgs += "/reference:$file"
}
$cscArgs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
& $compiler @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
