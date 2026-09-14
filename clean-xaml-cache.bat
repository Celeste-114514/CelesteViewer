@echo off
chcp 65001 >nul
setlocal

REM ============================================================
REM  XAML 编译缓存一键清理
REM
REM  什么时候用它：
REM    VS 报错 WMC9999 / WMC1509 / "未找到索引" / "Invalid metadata token"，
REM    而且报错的文件是
REM        ...microsoft.windowsappsdk.winui\<ver>\buildTransitive\
REM        Microsoft.UI.Xaml.Markup.Compiler.interop.targets
REM    而不是你写的某个 .xaml —— 那就是 XAML 增量缓存被写坏了，不是代码有错。
REM
REM  为什么会坏：
REM    XAML 编译器把共享增量状态写在 obj\...\XamlSavedState，
REM    VS 的设计时构建（打开项目、改 XAML 自动跑）和外部 dotnet build
REM    会同时读写同一份，撞上就写坏。WASDK 的 targets 里没有开关能关掉它，
REM    所以只能靠"两边别同时跑 + 坏了就清"。
REM
REM  注意：删的是 obj / bin 里的**构建产物**，源码一行不动，
REM  下次生成会全部重建（慢几十秒）。不是数据丢失。
REM ============================================================

cd /d "%~dp0"

echo.
echo   XAML 编译缓存清理
echo   ==================
echo.

REM ---- 1. VS 开着就先劝退：不然它会在你删完的瞬间写回一份坏的 ----
tasklist 2>nul | findstr /i "devenv.exe" >nul
if %errorlevel%==0 (
    echo   [!] 检测到 Visual Studio 正在运行。
    echo.
    echo       请先关闭 VS，再运行本脚本。
    echo       否则你删完它立刻写回一份坏状态，等于白清。
    echo.
    pause
    exit /b 1
)

REM ---- 2. 顺手把还在跑的 MSBuild 也清掉 ----
tasklist 2>nul | findstr /i "MSBuild.exe VBCSCompiler.exe" >nul
if %errorlevel%==0 (
    echo   正在结束残留的编译进程...
    taskkill /f /im MSBuild.exe       >nul 2>&1
    taskkill /f /im VBCSCompiler.exe  >nul 2>&1
    timeout /t 1 /nobreak >nul
)

REM ---- 3. 清 obj / bin（按平台，项目用的是 x64）----
set CLEANED=0

for %%P in (x64 x86 ARM64) do (
    if exist "obj\%%P" (
        echo   删除 obj\%%P
        rd /s /q "obj\%%P" 2>nul
        set CLEANED=1
    )
    if exist "bin\%%P" (
        echo   删除 bin\%%P
        rd /s /q "bin\%%P" 2>nul
        set CLEANED=1
    )
)

REM 有时候平台目录不在 obj\<平台> 而在 obj 根下（AnyCPU 配置）
if exist "obj\Debug"  ( echo   删除 obj\Debug   & rd /s /q "obj\Debug"   2>nul & set CLEANED=1 )
if exist "obj\Release" ( echo   删除 obj\Release & rd /s /q "obj\Release" 2>nul & set CLEANED=1 )

REM ---- 4. 顽固文件（被占用时 rd 会失败，列出来告诉你）----
if exist "obj" (
    dir /s /b "obj\*.xbf" >nul 2>&1
    if %errorlevel%==0 (
        echo.
        echo   [!] obj 里还有 .xbf 残留，可能有进程占着。
        echo       关掉 VS 后重新运行本脚本即可。
    )
)

echo.
if "%CLEANED%"=="1" (
    echo   完成。现在重新打开 VS，用「生成」菜单里的
    echo   **重新生成解决方案**（不是「生成解决方案」）。
    echo.
    echo   第一次会慢一些（XAML 全部重编），之后就正常了。
) else (
    echo   没有找到需要清理的目录，可能已经清过了。
)
echo.
pause
