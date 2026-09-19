param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler was not found.' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist\TriSwitch' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/utf8output', '/codepage:65001',
    "/out:$output\TriSwitch.exe", "/win32manifest:$projectRoot\src\app.manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Runtime.Serialization.dll',
    "/r:$framework\WPF\UIAutomationClient.dll", "/r:$framework\WPF\UIAutomationTypes.dll", "/r:$framework\WPF\WindowsBase.dll")
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'dictionaries') -Destination $output -Recurse -Force
foreach ($file in @('README.md', 'THIRD-PARTY.md')) {
    $source = Join-Path $projectRoot $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $output -Force }
}
if ($Test) {
    $process = Start-Process -FilePath (Join-Path $output 'TriSwitch.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
    Get-Content -LiteralPath (Join-Path $output 'self-test-results.txt')
    if ($process.ExitCode -ne 0) { throw 'Self-tests failed.' }
}
Write-Output "Built: $output\TriSwitch.exe"
