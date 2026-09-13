# 安装包制作 + 应用内更新 —— 交接说明

和 CelesteMusicPlayer 完全同一套路子，两边可以对照着看。

## 一、发布链路

```
发布新版本
   ├─ 1. 改版本号  CelesteViewer.csproj 的 <Version>
   ├─ 2. 构建发布  dotnet publish -c Release ...（产出 publish 目录，框架依赖）
   ├─ 3. 打安装包  installer\CelesteViewer.nsi → CelesteViewer-Setup-<版本>.exe
   ├─ 4. 上传 GitHub Release，把 Setup-*.exe 作为 asset 附件上传（关键！）
   └─ 5. 用户「关于」→「检查更新」→ 读 latest API 的 assets 找到 Setup-*.exe
            →「下载更新」→ 带进度下载 → 弹出安装向导覆盖安装
```

## 二、发布形态：框架依赖（不自包含）

| 项 | 值 |
|---|---|
| .NET | 框架依赖，需要用户已装 .NET 9 桌面运行时 |
| Windows App SDK | 框架依赖，需要用户已装 WindowsAppRuntime 2.x |
| 安装目录 | `%LOCALAPPDATA%\Programs\CelesteViewer` |
| 注册表安装位置 | `HKCU\Software\CelesteViewer\InstallLocation` |
| 安装包文件名 | `CelesteViewer-Setup-{版本}.exe`（**必须含 Setup 且 .exe 结尾**） |
| 输出位置 | `C:\Users\admin\Desktop\CelesteViewer-Setup-{版本}.exe` |
| 卸载器 | `$INSTDIR\uninstall.exe`（卸载时默认保留用户数据） |

> 为什么不做自包含：体积会从 35MB 涨到几百 MB，而且现在只有一个看图器。
> 等将来音视频 + 图片合成一个大软件时再考虑改自包含 —— 改法见下。

**缺框架的提示**：安装脚本 `.onInit` 里会检测两项运行时，缺了弹窗提示并打开下载页：
- .NET 9：查 `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\9.*`
- WinAppSDK：查 MSIX 包 `Microsoft.WindowsAppRuntime.2`（PowerShell `Get-AppxPackage`）

## 三、命令

**发布（框架依赖，默认）**
```
dotnet publish CelesteViewer.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false
```

**打安装包**
```
makensis.exe installer\CelesteViewer.nsi
```

**将来要改成自包含**（csproj 里 `PublishTrimmed` 已按 `SelfContained` 自动判定，不用改文件）
```
dotnet publish ... -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```
> ⚠️ 注意 `PublishTrimmed` 只有在自包含下才合法，框架依赖开它会报 `NETSDK1102`。

## 四、发版要改的两处版本号（最容易漏）

1. `installer\CelesteViewer.nsi` 里的 `!define APP_VERSION "26.9.14"`
2. `CelesteViewer.csproj` 里的 `<Version>26.9.14.0</Version>`

GitHub Release 的 tag 用 `v` + 三段式，例如 `v26.9.14`。
更新功能比较的是**程序集版本号**和 tag，两者必须对应，否则会出现"永远检查不到更新"。
