param([switch]$CheckOnly, [ValidateSet('persistent','temporary')][string]$Mode = 'persistent')
$ErrorActionPreference = 'Stop'
function Find-Executable($Name, $Candidates) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}
$chrome = Find-Executable 'chrome.exe' @(
    $env:CHROME_BINARY,
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
)
$node = Find-Executable 'node.exe' @(
    (Join-Path $env:ProgramFiles 'nodejs\node.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Tuanjie Cowork\cli\bin\win32-x64\node.exe')
)
if ($CheckOnly) {
    [pscustomobject]@{ Chrome = $chrome; Node = $node; Controller = Test-Path -LiteralPath (Join-Path $PSScriptRoot 'chrome-launcher.mjs') }
    exit 0
}
if (-not $chrome) {
    Add-Type -AssemblyName System.Windows.Forms
    $picker = New-Object Windows.Forms.OpenFileDialog
    $picker.Title = '选择 Google Chrome'
    $picker.Filter = 'Chrome (chrome.exe)|chrome.exe'
    if ($picker.ShowDialog() -eq 'OK') { $chrome = $picker.FileName }
    $picker.Dispose()
    if (-not $chrome) { exit 1 }
}
if (-not $node) { Write-Host '需要 Node.js 22.4 或更高版本：https://nodejs.org/'; exit 1 }
& $node (Join-Path $PSScriptRoot 'chrome-launcher.mjs') --chrome $chrome --mode $Mode
exit $LASTEXITCODE
