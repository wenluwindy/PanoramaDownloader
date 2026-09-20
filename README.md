<div align="center">

# 全景图下载工具

**把 720 云全景，一键变成你的本地资产**

Windows 桌面端 · 瓦片包 + 等距柱状整图 · 本地登录 · 隐私不出机

<br/>

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?style=for-the-badge&logo=windows&logoColor=white)
![UI](https://img.shields.io/badge/UI-WPF%20%2B%20WebView2-0E7C86?style=for-the-badge)
![License](https://img.shields.io/badge/用途-授权%2F自有内容-E85D04?style=for-the-badge)

<br/>

```text
  URL  →  登录（如需）  →  场景识别  →  下载瓦片  →  拼接整图
```

</div>

---

## 为什么用它

在 720 云上浏览全景很方便，但要把作品**可靠地落到本地**——完整瓦片、可二次创作的 equirectangular 整图、登录态不丢——往往要折腾半天。

**全景图下载工具**把这条链路收成一次流畅操作：粘贴链接，分析场景，勾选导出，进度条走完，文件就在你指定的文件夹里。

| | |
|:---|:---|
| **给个人** | 对自己有权使用的作品做本地存档 |
| **给创作者** | 导出素材，丢进 PS / AE / 全景工具继续做 |
| **给团队** | 归档自有展厅、房产、活动全景，不依赖网页随时可访问 |

---

## 核心能力

<table>
<tr>
<td width="50%" valign="top">

### 智能检测
粘贴 720 云作品链接，自动识别标题、场景列表、多分辨率瓦片与缩略图。

</td>
<td width="50%" valign="top">

### 内嵌登录
需登录时打开官方页面，Cookie 仅存本机 WebView2，**不上传到任何服务器**。

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 双模式导出
- **瓦片包** — 规范化目录 + `manifest.json`
- **等距柱状整图** — JPG / PNG，2:1 标准全景图

</td>
<td width="50%" valign="top">

### 稳妥下载
并发可控、失败重试、断点续传；单瓦片失败不拖垮整次任务。

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 预览确认
场景缩略图列表 + 大图确认；支持 3D 旋转预览，导出前心里有数。

</td>
<td width="50%" valign="top">

### 本地设置
下载目录、并发数、超时重试、默认导出模式、JPEG 质量等，一次配好反复用。

</td>
</tr>
</table>

---

## 使用流程

```mermaid
flowchart LR
  A[粘贴作品 URL] --> B{需要登录?}
  B -->|是| C[内嵌官方登录]
  B -->|否| D[分析场景]
  C --> D
  D --> E[选择场景与导出方式]
  E --> F[并发下载瓦片]
  F --> G[可选：拼接整图]
  G --> H[保存到本地]
```

1. **打开软件**，首次启动阅读并同意使用声明  
2. **粘贴** 720 云作品 HTTPS 链接，点击「分析」  
3. 若提示权限不足，点「登录 720 云」在官方页完成登录后重试  
4. 在场景列表中确认缩略图，选择层级与导出模式  
5. 点击「导出瓦片 / 导出整图 / 全部导出」，等待完成  

---

## 快速开始

### 运行环境

| 项目 | 要求 |
|------|------|
| 系统 | Windows 10 (1809+) / Windows 11，**x64** |
| 运行时 | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 浏览器组件 | [WebView2 Runtime Evergreen](https://developer.microsoft.com/microsoft-edge/webview2/)（多数 Win10/11 已自带） |

### 开发者运行

```powershell
dotnet restore
dotnet build
dotnet run --project src/PanoramaDownloader.App
```

### 跑测试

```powershell
dotnet test
```

### 一键发布

```powershell
powershell -ExecutionPolicy Bypass -File .\pack\publish.ps1
```

产物默认在 `dist\win-x64\`，详见 [pack/README.md](./pack/README.md)。

需要自带 .NET 运行时的包时：

```powershell
.\pack\publish.ps1 -SelfContained
```

---

## 技术架构

<div align="center">

| 层级 | 选型 |
|:----:|:-----|
| 运行时 | .NET 8 |
| 界面 | WPF + MVVM（CommunityToolkit.Mvvm） |
| 内嵌浏览 | Microsoft Edge WebView2 |
| 图像拼接 | SkiaSharp（分块写出，控制峰值内存） |
| 测试 | xUnit |

</div>

```text
┌─────────────────────────────────────────────────┐
│              WPF 主界面（MVVM）                   │
│     URL · 登录 · 场景 · 预览 · 进度 · 设置        │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│                   核心引擎                        │
│  WebView2 加载/捕获  →  Yun720 检测  →  下载/拼接  │
│              PanoramaManifest（统一清单）          │
└─────────────────────────────────────────────────┘
```

### 仓库结构

```text
全景图下载工具/
├── src/
│   ├── PanoramaDownloader.App/      # WPF 界面 + WebView2
│   └── PanoramaDownloader.Core/     # 检测 / 下载 / 拼接 / 设置
├── tests/
│   └── PanoramaDownloader.Tests/
├── pack/                            # 发布脚本
├── 软件开发文档.md
└── README.md
```

---

## 支持范围

| 内容 | 状态 |
|------|:----:|
| 720 云 — 公开可访问作品 | ✅ |
| 720 云 — 登录后可正常观看的作品 | ✅ |
| 720 云 — 付费加密 / DRM / 官方无法观看 | ❌ |
| 其它平台（Krpano 等） | 规划中 |

> 本工具**不是**爬虫服务，也**不会**破解付费或 DRM。登录由你在官方页面亲自完成。

---

## 合规与隐私

- 仅用于你**有权访问或自有**的内容，做本地存档或授权范围内的二次制作  
- 登录 Cookie **只保存在本机**，不上传第三方  
- 首次启动需确认使用声明；关于页可随时查看免责说明  

请勿用于未授权内容的抓取、传播或违规商业用途。

---

## 文档

- [软件开发文档](./软件开发文档.md) — 需求、架构与验收基线  
- [发布说明](./pack/README.md) — 打包参数与运行依赖  

---

<div align="center">

**把全景留在本地，随时打开、随时再创作。**

<sub>全景图下载工具 · Windows · .NET 8</sub>

</div>
