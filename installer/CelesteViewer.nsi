; CelesteViewer installer script (NSIS 3.x, Modern UI 2, 简体中文)
; Build: makensis.exe installer\CelesteViewer.nsi
;
; 与 CelesteMusicPlayer 的安装包同一套做法：
;   用户级安装（不用管理员）、装在 %LOCALAPPDATA%\Programs\ 下、
;   卸载时可选保留用户数据、文件名必须形如 CelesteViewer-Setup-<版本>.exe
;   —— 应用内「检查更新」靠文件名里的 Setup 认出这是安装包。
;
; 发布形态是**框架依赖**（跟 MusicPlayer 一样，不自包含）：
;   用户机器上必须已有 .NET 9 桌面运行时 和 Windows App SDK 2.x 运行时，
;   否则双击没反应。所以安装前先查这两个，缺哪个就提示去下载哪个。

; ---------- Metadata ----------
!define APP_NAME "CelesteViewer"
!define APP_VERSION "26.9.14"
!define APP_EXE "CelesteViewer.exe"
!define PUBLISH_DIR "C:\Users\admin\source\repos\CelesteViewer\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish"
!define APP_GUID "{7B4E1D92-3C5A-4F18-9D60-2A8C7E5B41F3}"
!define REG_UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define REG_RUN "Software\Microsoft\Windows\CurrentVersion\Run"

!define DOTNET_URL "https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0/runtime"
!define WASDK_URL "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"

Unicode true
; User-level install, no admin needed
RequestExecutionLevel user
Name "${APP_NAME}"
OutFile "C:\Users\admin\Desktop\CelesteViewer-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallLocation"
SetCompressor lzma

; ---------- Includes ----------
!include "LogicLib.nsh"
!include "MUI2.nsh"

; ---------- Modern UI 2 ----------
!insertmacro MUI_PAGE_WELCOME
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

; 列出缺哪些运行组件，结果拼进 $MissingList（为空 = 什么都有）
Function DetectRuntimes
  StrCpy $MissingList ""

  ; ---- 1. .NET 9 桌面运行时 ----
  ; 判据：dotnet 安装目录下存在 shared\Microsoft.WindowsDesktop.App\9.* 子目录。
  ; 不看注册表是因为不同安装方式（SDK / 运行时 / 独立安装器）写的位置不统一，
  ; 而 shared 目录是运行时一定会建的。
  StrCpy $R0 "0"
  FindFirst $R1 $R2 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\9.*"
net9_loop:
  StrCmp $R2 "" net9_done
  StrCpy $R0 "1"
  FindNext $R1 $R2
  Goto net9_loop
net9_done:
  FindClose $R1
  StrCmp $R0 "1" net9_ok
  StrCpy $MissingList "$MissingList  · .NET 9 桌面运行时（.NET 9 Desktop Runtime）$\r$\n"
net9_ok:

  ; ---- 2. Windows App SDK 2.x 运行时 ----
  ; 判据：系统里装了名为 Microsoft.WindowsAppRuntime.2 的 MSIX 包。
  ; 这东西没有稳定的注册表/文件位置可查，只能问 PowerShell。
  ; nsExec 第一个 Pop 是退出码；命令本身没跑起来时是字符串 "error"。
  nsExec::ExecToStack "powershell.exe -NoProfile -Command $\"if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2') { exit 0 } else { exit 1 }$\""
  Pop $R0
  Pop $R1
  StrCmp $R0 "0" wasdk_ok
  StrCmp $R0 "error" wasdk_ok        ; 查不出来就别吓唬人，当它装了
  StrCpy $MissingList "$MissingList  · Windows App SDK 运行时（WindowsAppRuntime 2.x）$\r$\n"
wasdk_ok:
FunctionEnd

Function .onInit
  Call DetectRuntimes
  StrCmp $MissingList "" all_ok

  MessageBox MB_YESNO|MB_ICONEXCLAMATION \
    "检测到这台电脑上还缺以下运行组件：$\r$\n$\r$\n$MissingList$\r$\n缺了它们，装完双击也是没反应。$\r$\n$\r$\n现在打开下载页面？（装好后重新运行本安装包即可）" \
    IDNO skip_open

  ; 两个页面都开，用户按提示里的顺序装就行
  ExecShell "open" "${DOTNET_URL}"
  ExecShell "open" "${WASDK_URL}"
  Quit

skip_open:
  MessageBox MB_OK|MB_ICONINFORMATION \
    "已跳过下载。安装会继续，但运行组件补齐之前程序无法启动。"

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

Section "开机自动启动" SEC_AUTORUN
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
