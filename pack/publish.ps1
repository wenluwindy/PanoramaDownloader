param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDir = "",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "dist\win-x64"
}

$project = Join-Path $root "src\PanoramaDownloader.App\PanoramaDownloader.App.csproj"
if (-not (Test-Path $project)) {
    throw "找不到项目文件：$project"
}

Write-Host "==> 还原与测试" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests\PanoramaDownloader.Tests\PanoramaDownloader.Tests.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "单元测试失败，已中止发布。"
}

$rid = "win-x64"
$self = if ($SelfContained) { "true" } else { "false" }

Write-Host "==> 发布单文件 ($Configuration, RID=$rid, SelfContained=$self)" -ForegroundColor Cyan
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $rid `
    --self-contained $self `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败。"
}

$readme = @"
全景图下载工具 — 运行说明
========================

1. 系统要求
   - Windows 10 1809+ / Windows 11，x64
   - Microsoft Edge WebView2 Runtime（Evergreen）
$(if (-not $SelfContained) { "   - .NET 8 Desktop Runtime (x64)`n" } else { "" })
2. 安装 WebView2（若启动提示缺少 Runtime）
   https://developer.microsoft.com/microsoft-edge/webview2/

3. 使用
   - 运行「全景图下载工具.exe」
   - 首次启动需同意使用声明
   - 打开 720 云作品 URL → 等待全景加载 → 分析 → 导出
   - 「设置」可调整下载目录、并发、重试、整图宽度等

4. 合规
   仅用于您有权访问或自有内容的本地导出，请勿抓取未授权作品。

发布配置：$Configuration / $rid / SelfContained=$self
"@

Set-Content -Path (Join-Path $OutputDir "README-运行说明.txt") -Value $readme -Encoding UTF8

Write-Host "==> 完成：$OutputDir" -ForegroundColor Green
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
