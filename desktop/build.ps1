param([string]$NodePath)
$ErrorActionPreference = 'Stop'
if (-not $NodePath) {
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($nodeCommand) { $NodePath = $nodeCommand.Source }
}
if (-not $NodePath) { throw 'Pass -NodePath with the path to Node.js v24.16.0 for Windows x64' }
if ((& $NodePath --version) -ne 'v24.16.0') { throw 'The bundled NODE-LICENSE.txt targets Node.js v24.16.0; use this version to build.' }
$desktopRoot = $PSScriptRoot
$sourceRoot = Split-Path -Parent $desktopRoot
$buildRoot = Join-Path $desktopRoot 'build'
$distRoot = Join-Path $sourceRoot 'dist'
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
[IO.Directory]::CreateDirectory($distRoot) | Out-Null
if (-not (Test-Path -LiteralPath $NodePath)) { throw 'Node executable missing' }
if (-not (Test-Path -LiteralPath (Join-Path $desktopRoot 'NODE-LICENSE.txt'))) { throw 'Node license missing' }
$compressedPath = Join-Path $buildRoot 'node.exe.gz'
$inputStream = [IO.File]::OpenRead($NodePath)
$outputStream = [IO.File]::Create($compressedPath)
$gzip = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::Optimal)
try { $inputStream.CopyTo($gzip) } finally { $gzip.Dispose(); $outputStream.Dispose(); $inputStream.Dispose() }
$assets = @{
    'node.exe' = $NodePath
    'chrome-launcher.mjs' = (Join-Path $sourceRoot 'chrome-launcher.mjs')
    'check.html' = (Join-Path $sourceRoot 'check.html')
    'smoke-test.mjs' = (Join-Path $sourceRoot 'smoke-test.mjs')
    'NODE-LICENSE.txt' = (Join-Path $desktopRoot 'NODE-LICENSE.txt')
}
$hashes = foreach ($name in ($assets.Keys | Sort-Object)) { (Get-FileHash -LiteralPath $assets[$name] -Algorithm SHA256).Hash.ToLowerInvariant() + ' ' + $name }
$manifestPath = Join-Path $buildRoot 'hashes.txt'
[IO.File]::WriteAllLines($manifestPath, $hashes, [Text.UTF8Encoding]::new($false))
$exePath = Join-Path $distRoot 'ChromePrivacy.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.IO.Compression.dll', ('/win32manifest:' + (Join-Path $desktopRoot 'app.manifest')), ('/out:' + $exePath), ('/resource:' + $manifestPath + ',Payload.hashes.txt'), ('/resource:' + $compressedPath + ',Payload.node.exe.gz'))
foreach ($name in $assets.Keys) {
    if ($name -ne 'node.exe') { $compilerArgs += '/resource:' + $assets[$name] + ',Payload.' + $name }
}
$compilerArgs += Join-Path $desktopRoot 'ChromePrivacy.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Get-Item -LiteralPath $exePath | Select-Object FullName,Length
Get-FileHash -LiteralPath $exePath -Algorithm SHA256
