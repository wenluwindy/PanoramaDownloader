# 发布说明

本目录存放安装/发布相关脚本。

## 依赖

- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)（框架依赖发布）
- [WebView2 Runtime Evergreen](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10/11 多数已自带）

## 一键发布

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\pack\publish.ps1
```

产物默认输出到 `dist\win-x64\`：

- `全景图下载工具.exe`（单文件）
- `README-运行说明.txt`

可选参数：

```powershell
.\pack\publish.ps1 -Configuration Release -OutputDir "D:\Release\pano"
.\pack\publish.ps1 -SelfContained   # 自带 .NET 运行时，体积更大
```
