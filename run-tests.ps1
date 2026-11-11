param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'IndustrialDataGridView.sln'
$testDll = Join-Path $root "src\IndustrialDataGridView.Tests\bin\$Configuration\IndustrialDataGridView.Tests.dll"

$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
$vstest = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\Extensions\TestPlatform\vstest.console.exe'

foreach ($tool in @($msbuild, $vstest)) {
    if (!(Test-Path -LiteralPath $tool)) {
        Write-Error "缺少工具：$tool"
        exit 2
    }
}

if (!$NoBuild) {
    & $msbuild $solution "/t:Restore" "/p:Configuration=$Configuration" /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'NuGet 还原失败'
        exit 1
    }

    & $msbuild $solution "/p:Configuration=$Configuration" /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error '编译失败'
        exit 1
    }
}

$arguments = @($testDll, '/Platform:x86')
if ($Filter) {
    $arguments += "/TestCaseFilter:$Filter"
}
& $vstest @arguments
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Write-Output "有测试未通过（vstest 退出码 $exitCode）"
    exit $exitCode
}
Write-Output '全部测试通过。'
