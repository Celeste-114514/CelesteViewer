; CelesteViewer installer script (NSIS 3.x, Modern UI 2, 简体中文)
; Build: makensis.exe installer\CelesteViewer.nsi
;
; 与 CelesteMusicPlayer 的安装包同一套做法：
;   用户级安装（不用管理员）、装在 %LOCALAPPDATA%\Programs\ 下、
;   卸载时可选保留用户数据、文件名必须形如 CelesteViewer-Setup-<版本>.exe
;   —— 应用内「检查更新」靠文件名里的 Setup 认出这是安装包。
;
; 发布形态是**框架依赖**（跟 MusicPlayer 一样，不自包含）：
;   用户机器上必须已有 .NET 9 运行时 和 Windows App SDK 2.x 运行时，
;   否则双击没反应。所以安装前先查这两个，缺哪个就提示去下载哪个。

; ---------- Metadata ----------
!define APP_NAME "CelesteViewer"
!define APP_VERSION "26.9.14"
!define APP_EXE "CelesteViewer.exe"
; 发布目录注意带 x64 一层 —— 跟 CelesteViewer.csproj 里
; CelesteCopyPriToPublish 那个 target 的输出保持一致，
; 否则会打进一个缺 CelesteViewer.pri 的残缺发布目录（启动即崩）。
!define PUBLISH_DIR "C:\Users\admin\source\repos\CelesteViewer\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\publish"
!define APP_GUID "{7B4E1D92-3C5A-4F18-9D60-2A8C7E5B41F3}"
!define REG_UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define REG_RUN "Software\Microsoft\Windows\CurrentVersion\Run"

!define DOTNET_URL "https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0/runtime"
!define WASDK_URL "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"

; Windows App SDK 2.x 的框架包名形如
;   Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe
; 前半截是固定前缀（下面这个 define，长度正好 29 个字符，代码里按长度截取比对），
; 后半截带版本和架构，会随版本变化，所以只比前缀。
; 注意别把 Microsoft.WindowsAppRuntime.CBS.2 算进来 —— 那前缀对不上，天然排除。
!define WASDK_PKG_PREFIX "Microsoft.WindowsAppRuntime.2"
; MSIX 包对当前用户注册后，包名会作为键名落在下面这个注册表路径里。
; 这是**不启动任何外部程序**就能查到包列表的地方，用来当主判据。
!define APPX_REPO "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"

Unicode true
; User-level install, no admin needed
RequestExecutionLevel user
Name "${APP_NAME}"
; 安装包落在项目的 dist\ 目录（不往桌面丢东西），上传 GitHub Release 也从这里取。
; 文件名必须是 CelesteViewer-Setup-<版本>.exe —— 应用内「检查更新」靠 Setup 字样认出安装包。
OutFile "C:\Users\admin\source\repos\CelesteViewer\dist\CelesteViewer-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallLocation"
SetCompressor lzma

; ---------- Includes ----------
!include "LogicLib.nsh"
!include "MUI2.nsh"

; ---------- Modern UI 2 ----------
!insertmacro MUI_PAGE_WELCOME

; 离开"选择安装位置"页之前，先确认这个位置真能写。
; 细节和原因见下面的 CheckDirWritable 函数 —— 不加这道校验的话，
; 用户把路径选到 C:\Program Files\ 只会得到一个看不懂的
; "无法打开要写入的文件"，根本猜不到是权限问题（2026-09-14 实际踩到）。
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckDirWritable
!insertmacro MUI_PAGE_DIRECTORY

!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"
!define MUI_FINISHPAGE_RUN_CHECKED
!insertmacro MUI_PAGE_FINISH

; ---------- Uninstall pages ----------
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "卸载完成"
!define MUI_FINISHPAGE_TEXT "本程序已从您的电脑卸载。"
!insertmacro MUI_UNPAGE_FINISH

; ---------- Languages ----------
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------- 运行时检测 ----------
Var MissingList
Var MissingDotnet
Var MissingWasdk
Var DirProbe
Var ProbeIdx          ; 枚举注册表包仓库时的下标
Var ProbeKey          ; 枚举出来的键名
Var WasdkRepoHit      ; 注册表里是否找到 WindowsAppRuntime 2.x（1=找到）
Var PsExitCode        ; 兜底问 PowerShell 时它的原始退出码（诊断用）

; ---------- 安装位置可写性校验 ----------
; 在"选择安装位置"页点「下一步」时调用（MUI_PAGE_CUSTOMFUNCTION_LEAVE）。
;
; 为什么要有这道校验：
;   本安装包是**免管理员**运行的（RequestExecutionLevel user），默认装在
;   %LOCALAPPDATA%\Programs\ 下，全程不弹 UAC —— 这是和 CelesteMusicPlayer
;   保持一致的设计。
;   但向导允许改路径，而"装到 C:\Program Files\" 是很多人的习惯。普通权限
;   根本写不进那个目录，NSIS 报出来的却是 "无法打开要写入的文件" —— 完全
;   看不出跟权限有关，用户只会以为安装包坏了。
;   所以在这一步就拦住，说清原因、给出能走的路，而不是等复制文件时才炸。
;
; 校验方式：真往目标目录写一个临时文件，写得进去才算过。
;   比读 ACL 可靠 —— 不关心"为什么不能写"（权限不足 / 只读属性 / 组策略），
;   只要写不进去，安装过程就一定会失败。
Function CheckDirWritable
  CreateDirectory "$INSTDIR"

  ClearErrors
  FileOpen $DirProbe "$INSTDIR\.cv_write_test.tmp" w
  IfErrors dir_not_writable
  FileClose $DirProbe
  Delete "$INSTDIR\.cv_write_test.tmp"
  Goto dir_check_done

dir_not_writable:
  MessageBox MB_YESNO|MB_ICONEXCLAMATION \
"这个位置装不进去：$\r$\n$\r$\n$INSTDIR$\r$\n$\r$\n它需要管理员权限，而本安装程序是免管理员运行的，往这里写文件会被系统拒绝（就是“无法打开要写入的文件”这个报错）。$\r$\n$\r$\n推荐位置：$LOCALAPPDATA\Programs\${APP_NAME}$\r$\n$\r$\n点「是」= 用推荐位置继续安装$\r$\n点「否」= 退回去，自己换一个能写的位置（比如 D 盘建个文件夹）$\r$\n$\r$\n如果确实要装在这个位置：先关掉本窗口，右键安装包选“以管理员身份运行”。" \
    IDYES dir_use_default IDNO dir_keep_user

dir_keep_user:
  Abort        ; 留在目录页，让用户自己改

dir_use_default:
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APP_NAME}"

dir_check_done:
FunctionEnd

; 查缺哪些运行组件。$MissingList 是给用户看的清单（为空 = 什么都有），
; $MissingDotnet / $MissingWasdk 是标志位，用来决定打开哪个下载页。
Function DetectRuntimes
  StrCpy $MissingList ""
  StrCpy $MissingDotnet "0"
  StrCpy $MissingWasdk "0"

  ; ---- 1. .NET 9 运行时 ----
  ; 判据：dotnet 的 shared 目录下存在 Microsoft.NETCore.App\9.* 子目录。
  ;
  ; 为什么查 NETCore.App 而不是 WindowsDesktop.App：
  ;   本项目发布产物的 runtimeconfig.json 里只声明了一个框架 ——
  ;     "framework": { "name": "Microsoft.NETCore.App", "version": "9.0.0" }
  ;   因为主项目只开了 UseWinUI，没开 UseWPF / UseWindowsForms。
  ;   （对照：CelesteMusicPlayer 的 runtimeconfig 声明了 NETCore.App +
  ;    WindowsDesktop.App 两个，所以它那边查 Desktop 才对。）
  ;   本项目查 Desktop 是错的判据：装齐了 NETCore.App 也会被误报成"缺运行时"。
  ;
  ; 为什么不查注册表：.NET 的注册表项只记录"装过 host"，不区分装了哪些运行时版本，
  ; 而 shared 目录是运行时落地时一定会建的，最准。
  ; 查两个位置是为了覆盖安装方式差异 —— 官方安装器 / VS 装到 Program Files，
  ; winget --scope user 之类的会装到用户目录。
  StrCpy $R0 "0"

  ; 1a. 全机安装位置
  FindFirst $R1 $R2 "$PROGRAMFILES64\dotnet\shared\Microsoft.NETCore.App\9.*"
net9_loop:
  StrCmp $R2 "" net9_probe_user
  StrCpy $R0 "1"
  FindNext $R1 $R2
  Goto net9_loop

  ; 1b. 用户级安装位置
net9_probe_user:
  FindClose $R1
  StrCmp $R0 "1" net9_ok
  FindFirst $R1 $R2 "$LOCALAPPDATA\Microsoft\dotnet\shared\Microsoft.NETCore.App\9.*"
net9_loop2:
  StrCmp $R2 "" net9_done
  StrCpy $R0 "1"
  FindNext $R1 $R2
  Goto net9_loop2

net9_done:
  FindClose $R1
  StrCmp $R0 "1" net9_ok
  StrCpy $MissingDotnet "1"
  StrCpy $MissingList "$MissingList  · .NET 9 运行时（.NET 9 Runtime，装 Desktop 版最省事）$\r$\n"
net9_ok:

  ; ---- 2. Windows App SDK 2.x 运行时 ----
  ; 需要的包名是 Microsoft.WindowsAppRuntime.2（框架包，2.4.0.0 这种）。
  ;
  ; 【为什么改过一轮判据】
  ;   最初这里只问 PowerShell（Get-AppxPackage），并且把"退出码不是 0"一律当成
  ;   "没装"。实测发现这个判据太脆：只要那条命令有任何意外 —— 被安全软件拦下、
  ;   nsExec 拿不到退出码、PowerShell 起不来 —— 都会得到非 0 的结果，于是给
  ;   明明装好运行时的用户弹一句"缺少 Windows App SDK 运行时"（2026-09-14 用户实际踩到）。
  ;   检测手段失灵，不该变成一句吓人的误报。
  ;
  ; 【现在的判据】分主副两级，只要有一级说"装了"就算装了：
  ;   主：读注册表包仓库（EnumRegKey 遍历，纯 API、不开进程，最稳）。
  ;   副：兜底问一次 PowerShell，但**只有它明确回答 1（查了、确实没有）才算缺**。
  ;       返回别的任何值（0 / error / 超时 / 被拦）都当"装了"。
  ;
  ; 【为什么不查 HKLM 那边】实测 HKLM 同名路径下只有 1 个条目，且与 WASDK 无关；
  ;   而包对用户注册后一定会出现在 HKCU 这条路径下（本机实测 278 个包里能查到）。
  StrCpy $WasdkRepoHit "0"
  StrCpy $ProbeIdx "0"
wasdk_reg_loop:
  EnumRegKey $ProbeKey HKCU "${APPX_REPO}" $ProbeIdx
  StrCmp $ProbeKey "" wasdk_reg_done
  StrCpy $R8 $ProbeKey 29                  ; 29 = "${WASDK_PKG_PREFIX}" 的长度
  StrCmp $R8 "${WASDK_PKG_PREFIX}" wasdk_reg_hit
  IntOp $ProbeIdx $ProbeIdx + 1
  Goto wasdk_reg_loop
wasdk_reg_hit:
  StrCpy $WasdkRepoHit "1"
wasdk_reg_done:
  StrCmp $WasdkRepoHit "1" wasdk_ok

  ; 兜底：nsExec 第一个 Pop 是退出码；命令本身没跑起来时是字符串 "error"。
  nsExec::ExecToStack /TIMEOUT=30000 "powershell.exe -NoProfile -Command $\"if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2') { exit 0 } else { exit 1 }$\""
  Pop $PsExitCode
  Pop $R1
  StrCmp $PsExitCode "1" 0 wasdk_ok        ; 只有明确回答 1 才算缺
  StrCpy $MissingWasdk "1"
  StrCpy $MissingList "$MissingList  · Windows App SDK 运行时（WindowsAppRuntime 2.x）$\r$\n"
wasdk_ok:
FunctionEnd

; 把检测过程记一份到 %TEMP%，万一以后还有误报，直接看这个文件就知道
; 每级判据实际返回了什么，不用再猜。
Function WriteDiagLog
  FileOpen $9 "$TEMP\CelesteViewer-setup-diag.log" w
  FileWrite $9 "--- CelesteViewer 安装包运行时检测 ---$\r$\n"
  FileWrite $9 "MissingDotnet = $MissingDotnet  (1 = 判定为缺)$\r$\n"
  FileWrite $9 "MissingWasdk  = $MissingWasdk  (1 = 判定为缺)$\r$\n"
  FileWrite $9 "WasdkRepoHit  = $WasdkRepoHit  (1 = 注册表里查到了 WindowsAppRuntime 2.x)$\r$\n"
  FileWrite $9 "PsExitCode    = $PsExitCode  (PowerShell 兜底查询的退出码)$\r$\n"
  FileWrite $9 "MissingList   = $MissingList$\r$\n"
  FileClose $9
FunctionEnd

Function .onInit
  Call DetectRuntimes
  Call WriteDiagLog                     ; 顺手记一份到 %TEMP%，方便事后排查
  StrCmp $MissingList "" all_ok

  MessageBox MB_YESNO|MB_ICONEXCLAMATION \
    "没有检测到下面这些运行组件：$\r$\n$\r$\n$MissingList$\r$\n本程序是“框架依赖”版，这两样是运行前提，缺了装完双击没反应。$\r$\n$\r$\n说明：这项检测是读注册表和系统包列表得出的，个别情况下可能不准。$\r$\n如果你确认装过、或者程序之前本来就能启动，点「否」直接继续安装即可，不影响安装结果。$\r$\n$\r$\n点「是」= 现在打开官方下载页面（装好后重新运行本安装包）$\r$\n点「否」= 先不管，继续安装" \
    IDNO skip_open

  ; 只开真正缺的那个页面 —— 缺一个就开一个，别一股脑弹两个标签
  StrCmp $MissingDotnet "1" 0 open_wasdk
  ExecShell "open" "${DOTNET_URL}"
open_wasdk:
  StrCmp $MissingWasdk "1" 0 open_done
  ExecShell "open" "${WASDK_URL}"
open_done:
  Quit

skip_open:
  MessageBox MB_OK|MB_ICONINFORMATION \
    "已跳过下载。安装会继续，但运行组件补齐之前程序无法启动。$\r$\n$\r$\n补齐后直接打开 ${APP_NAME} 即可，不用重装。"

all_ok:
FunctionEnd

; ---------- Install sections (components) ----------
Section "看图器主程序（必需）" SEC_APP
  SectionIn RO

  ; 更新场景：先关掉正在运行的旧版本，释放 CelesteViewer.exe 的文件锁，否则覆盖安装会失败
  ExecWait 'taskkill /F /IM CelesteViewer.exe'
  Sleep 1000

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PUBLISH_DIR}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Add/Remove Programs (user-level)
  WriteRegStr HKCU "${REG_UNINST}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${REG_UNINST}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${REG_UNINST}" "Publisher" "${APP_NAME}"
  WriteRegStr HKCU "${REG_UNINST}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${REG_UNINST}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${REG_UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${REG_UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${REG_UNINST}" "NoRepair" 1
  WriteRegStr HKCU "Software\${APP_NAME}" "InstallLocation" "$INSTDIR"
SectionEnd

Section "创建桌面快捷方式" SEC_DESKTOP
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "创建开始菜单快捷方式" SEC_STARTMENU
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0
SectionEnd

; /o = 默认不勾选。看图器没必要开机就在后台待着（那是音乐播放器的需求），
; 想让它自启的用户在安装时自己勾一下就行。
Section /o "开机自动启动" SEC_AUTORUN
  WriteRegStr HKCU "${REG_RUN}" "${APP_NAME}" '"$INSTDIR\${APP_EXE}"'
SectionEnd

; ---------- Section descriptions ----------
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "CelesteViewer 主程序和全部运行文件（必须安装）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在桌面创建启动快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTMENU} "在开始菜单创建启动与卸载快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTORUN} "登录 Windows 后自动启动 ${APP_NAME}。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- Uninstall ----------
Section "Uninstall"
  ; 卸载前先关闭可能仍在运行的程序，避免文件占用导致残留
  ExecWait 'taskkill /F /IM CelesteViewer.exe'
  Sleep 500

  ; 询问是否删除用户数据（仅在 GUI 交互模式弹出；静默 /S 卸载自动保留数据）
  IfSilent +3
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "是否同时删除用户数据（设置、图库列表、缩略图缓存、日志）？$\r$\n推荐选择“否”以保留数据。$\r$\n注意：删除后无法恢复。" IDYES del_userdata IDNO keep_userdata

  del_userdata:
    RMDir /r "$LOCALAPPDATA\${APP_NAME}"
    goto after_userdata
  keep_userdata:
  after_userdata:

  ; 删除开机自启
  DeleteRegValue HKCU "${REG_RUN}" "${APP_NAME}"

  ; 删除快捷方式
  Delete "$DESKTOP\${APP_NAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APP_NAME}"

  ; 删除程序文件
  RMDir /r "$INSTDIR"

  ; 删除注册表
  DeleteRegKey HKCU "${REG_UNINST}"
  DeleteRegKey HKCU "Software\${APP_NAME}"
SectionEnd
