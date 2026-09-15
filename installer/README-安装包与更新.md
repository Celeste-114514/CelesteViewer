# 安装包制作 + 应用内更新 —— 交接说明

和 CelesteMusicPlayer 完全同一套路子，两边可以对照着看。

## 一、发布链路

```
发布新版本
   ├─ 1. 改版本号  CelesteGallery.csproj 的 <Version>
   ├─ 2. 构建发布  dotnet publish -c Release -p:Platform=x64（产出 publish 目录，框架依赖）
   ├─ 3. 打安装包  installer\CelesteGallery.nsi → dist\CelesteGallery-Setup-<版本>.exe
   ├─ 4. 上传 GitHub Release，把 Setup-*.exe 作为 asset 附件上传（关键！）
   └─ 5. 用户「关于」→「检查更新」→ 读 latest API 的 assets 找到 Setup-*.exe
            →「下载更新」→ 带进度下载 → 弹出安装向导覆盖安装
```

## 二、发布形态：框架依赖（不自包含）

| 项 | 值 |
|---|---|
| .NET | 框架依赖，需要用户已装 **.NET 9 运行时** |
| Windows App SDK | 框架依赖，需要用户已装 WindowsAppRuntime 2.x |
| 安装目录 | `%LOCALAPPDATA%\Programs\CelesteGallery` |
| 注册表安装位置 | `HKCU\Software\CelesteGallery\InstallLocation` |
| 安装包文件名 | `CelesteGallery-Setup-{版本}.exe`（**必须含 Setup 且 .exe 结尾**） |
| 输出位置 | `dist\CelesteGallery-Setup-{版本}.exe`（项目根下的 dist 目录） |
| 卸载器 | `$INSTDIR\uninstall.exe`（卸载时默认保留用户数据） |
| 产物体积 | publish 约 128MB → 安装包约 35MB |

> 为什么不做自包含：体积会从 35MB 涨到几百 MB，而且现在只有一个看图器。
> 等将来音视频 + 图片合成一个大软件时再考虑改自包含 —— 改法见下。

**缺框架的提示**：安装脚本 `.onInit` 里会检测两项运行时，缺了弹窗提示并**只打开缺的那一个**下载页：
- .NET 9：查 `%ProgramFiles%\dotnet\shared\Microsoft.NETCore.App\9.*`
  （同时兜底查 `%LOCALAPPDATA%\Microsoft\dotnet\shared\...`，覆盖 winget 用户级安装）
- WinAppSDK：查 MSIX 包 `Microsoft.WindowsAppRuntime.2`（PowerShell `Get-AppxPackage`）

> ⚠️ **.NET 的检测判据是 `Microsoft.NETCore.App`，不是 `WindowsDesktop.App`。**
> 本项目的 runtimeconfig.json 只声明了 `Microsoft.NETCore.App`（因为只开了 `UseWinUI`，
> 没开 WPF/WinForms）。写成 Desktop 会把装齐了运行时的机器误判成"缺运行时"。
> 对照：CelesteMusicPlayer 的 runtimeconfig 声明了两个（含 WindowsDesktop.App），
> 它那边查 Desktop 才对 —— 两边不要照抄检测条件。

## 三、命令

**发布（框架依赖，默认）**
```
dotnet publish CelesteGallery.csproj -c Release -p:Platform=x64
```
> 不用再传 `-p:RuntimeIdentifier` / `-p:SelfContained`：
> `Properties\PublishProfiles\win-x64.pubxml` 里已经设好 `RuntimeIdentifier=win-x64`、
> `SelfContained=false`。命令行只在要临时改形态时才覆盖。

**打安装包**
```
makensis.exe installer\CelesteGallery.nsi
```

**将来要改成自包含**（csproj 里 `PublishTrimmed` 已按 `SelfContained` 自动判定，不用改文件）
```
dotnet publish CelesteGallery.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```
> ⚠️ 注意 `PublishTrimmed` 只有在自包含下才合法，框架依赖开它会报 `NETSDK1102`。

## 四、发布时最容易踩的坑：PRI 资源索引会丢

**症状**：`dotnet publish` 不报任何错、产物看着齐全（128MB / 56 个文件），
但发布目录里**没有 `CelesteGallery.pri`**。装到机器上一点开就死在
`MainWindow` 的 `LoadComponent`，日志只有一句 `0x80004005`，看不出跟资源有关。

**原因**：本项目为了避开 VS 设计时构建的 `WMC9999`，关掉了 MSIX 工具链
（`<EnableMsixTooling>false</EnableMsixTooling>`）。代价是 AppX/PRI 那套 target 也不跑了，
而"把 `*.pri` 加进待发布列表"正是它们干的活。

**为什么开发时发现不了**：Debug / Release 的 **build** 输出目录里 pri 是有的，能正常跑；
只有走 **publish** 才会丢。所以本地调试一切正常，一发布就崩。

**已修的修法**：`CelesteGallery.csproj` 末尾加了 `CelesteCopyPriToPublish` target，
publish 收尾时自动把 `$(OutDir)$(MSBuildProjectName).pri` 复制进 `$(PublishDir)`。
发布日志里会打印一行 `CelesteGallery: PRI 资源索引已补入发布目录 → ...`；
如果源文件不存在会直接给一个 **warning**，不会再静默产出残缺包。

**另外**：`win-x64.pubxml` 里的 `PublishDir` 原本是 MSIX 模板默认值
（`bin\$(Configuration)\$(TargetFramework)\$(RuntimeIdentifier)\publish\`，不含平台层），
现已补上 `$(Platform)` 一层，与 build 的 OutDir 体系对齐。
发布目录因此是 `bin\x64\Release\<tfm>\win-x64\publish\`（**注意有 x64 这一层**），
`installer\CelesteGallery.nsi` 里的 `PUBLISH_DIR` 必须与之保持一致。

## 五、发版要改的两处版本号（最容易漏）

1. `installer\CelesteGallery.nsi` 里的 `!define APP_VERSION "26.9.14"`
2. `CelesteGallery.csproj` 里的 `<Version>26.9.14.0</Version>`

GitHub Release 的 tag 用 `v` + 三段式，例如 `v26.9.14`。
更新功能比较的是**程序集版本号**和 tag，两者必须对应，否则会出现"永远检查不到更新"。
