[CmdletBinding()]
param(
    [string] $Checkout = (Join-Path $PSScriptRoot '../work/ivmp-ce-upstream'),
    [string] $MSBuild,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $Toolset = 'v141'
)

$ErrorActionPreference = 'Stop'
if (-not $MSBuild) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $locator) {
        $MSBuild = & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
    }
    if (-not $MSBuild) {
        $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
        if ($command) { $MSBuild = $command.Source }
    }
}
if (-not $MSBuild) { throw 'MSBuild with the x86 C++ Build Tools was not found.' }

python (Join-Path $PSScriptRoot 'tools/prepare.py') $Checkout
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$solution = Join-Path $Checkout 'IVMP.sln'
& $MSBuild $solution '/m' '/verbosity:minimal' "/p:Configuration=$Configuration" '/p:Platform=Win32' "/p:PlatformToolset=$Toolset" '/p:WindowsTargetPlatformVersion=10.0'
exit $LASTEXITCODE
