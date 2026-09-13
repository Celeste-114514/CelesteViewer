# CelesteViewer

为本地图片打造的 Windows 看图器，WinUI 3 / C# / .NET 9。

## 功能

- **浏览**：虚拟化缩略图墙 + 磁盘缓存（二次打开快很多）、方形/等高 × 小/中/大、左侧目录树懒加载、图库管理
- **看图**：锚点缩放、翻页、全屏、幻灯片放映、独立看图窗口、OCR 提取图上文字
- **编辑**：旋转/翻面/改尺寸/另存/复制/打印、裁剪、标记（含荧光笔、橡皮）、选区擦除（智能填充）、背景虚化/抠除/替换（含魔棒）、10 项调整滑块、13 种滤镜
- **其它**：压缩包内直接翻图（zip / cbz）、文件关联设置、三段式关于页 + 在线更新

常见格式走系统自带的 WIC 解码，不额外占体积；HEIC / AVIF / 相机 RAW / PSD 这类由 Magick.NET 兜底。
图片处理全部在本机完成、不上传；只有「检查更新」时会联网。

## 安装

从 [Releases](https://github.com/Celeste-114514/CelesteViewer/releases) 下载 `CelesteViewer-Setup-<版本>.exe` 运行即可。

程序是**框架依赖**发布，运行前需要：

- .NET 9 桌面运行时 —— https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0/runtime
- Windows App SDK 2.x 运行时 —— https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads

安装程序会在开始前检测这两项，缺了会提示下载。

## 构建

```
dotnet build CelesteViewer.csproj -c Debug -p:Platform=x64
```

打安装包（需 NSIS）：

```
dotnet publish CelesteViewer.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false
makensis.exe installer\CelesteViewer.nsi
```

发布流程详见 `installer/README-安装包与更新.md`。

## 关于

作者 Celeste，一名苦逼大学生。反馈：t.me/celesteabsolomb
