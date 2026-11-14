param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [string]$Filter = '',
    [string]$MSBuildPath = '',
    [string]$VSTestPath = '',
    [switch]$DisableSharedCompilation
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'IndustrialDataGridView.sln'
$testDll = Join-Path $root "src\IndustrialDataGridView.Tests\bin\$Configuration\IndustrialDataGridView.Tests.dll"

function Resolve-VsTool([string]$ExplicitPath, [string]$Name, [string]$Pattern) {
    if ($ExplicitPath) {
        if (!(Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) { throw "工具不存在：$ExplicitPath" }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products '*' -find $Pattern | Select-Object -First 1
        if ($found) { return $found }
    }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw "未找到 $Name，请安装 VS/Build Tools 的相应组件，或传入工具路径。"
}

$vstest = Resolve-VsTool $VSTestPath 'vstest.console.exe' 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
Write-Output "测试运行器：$vstest"

if (!$NoBuild) {
    $msbuild = Resolve-VsTool $MSBuildPath 'MSBuild.exe' 'MSBuild\**\Bin\MSBuild.exe'
    Write-Output "构建工具：$msbuild"
    $compilerArguments = @()
    if ($DisableSharedCompilation) { $compilerArguments += '/p:UseSharedCompilation=false' }
    & $msbuild $solution "/t:Restore" "/p:Configuration=$Configuration" /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'NuGet 还原失败'
        exit 1
    }

    & $msbuild $solution "/p:Configuration=$Configuration" @compilerArguments /v:m /nologo
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
