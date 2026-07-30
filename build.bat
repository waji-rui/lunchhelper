@echo off
setlocal

REM 脚本所在目录（去掉尾部反斜杠，避免 "%~dp0" 末尾 \ 转义闭合引号导致传给 PowerShell 的参数含非法字符）
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

REM ============================================================
REM  LunchHelper build script (Windows plus .NET Framework 4.8)
REM  Requires Visual Studio with the .NET desktop development
REM  workload (Roslyn C# compiler). Detection is version-agnostic:
REM  vswhere finds any installed instance automatically, then a
REM  directory scan covers the rest. Open LunchHelper.csproj in
REM  Visual Studio and press F6 as the simplest alternative.
REM ============================================================

set "BUILD="

REM 1) vswhere: write its output to a temp file, then read it back.
REM    This avoids the parenthesis and quoting pitfalls of running
REM    a command directly inside a for /f block, and auto-detects
REM    ANY Visual Studio version (2017/2019/2022/2026/Preview/...).
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
  "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\MSBuild.exe" > "%TEMP%\lh_msbuild.txt" 2>nul
)
if exist "%TEMP%\lh_msbuild.txt" (
  for /f "usebackq tokens=*" %%i in ("%TEMP%\lh_msbuild.txt") do (
    if not defined BUILD if exist "%%i" set "BUILD=%%i"
  )
  del /q "%TEMP%\lh_msbuild.txt" >nul 2>nul
)

REM 2) directory scan for any year and edition (version-agnostic).
REM    for /d enumerates whatever year subfolders exist now or later.
if not defined BUILD (
  for /d %%y in ("%ProgramFiles%\Microsoft Visual Studio\*") do (
    if not defined BUILD (
      for %%e in (Community Professional Enterprise BuildTools Preview) do (
        if not defined BUILD (
          if exist "%%y\%%e\MSBuild\Current\Bin\MSBuild.exe" (
            set "BUILD=%%y\%%e\MSBuild\Current\Bin\MSBuild.exe"
          )
        )
      )
    )
  )
)

if not defined BUILD (
  for /d %%y in ("%ProgramFiles(x86)%\Microsoft Visual Studio\*") do (
    if not defined BUILD (
      for %%e in (Community Professional Enterprise BuildTools Preview) do (
        if not defined BUILD (
          if exist "%%y\%%e\MSBuild\Current\Bin\MSBuild.exe" (
            set "BUILD=%%y\%%e\MSBuild\Current\Bin\MSBuild.exe"
          )
        )
      )
    )
  )
)

if not defined BUILD (
  echo ERROR: Visual Studio with the .NET desktop development
  echo workload was not found. This project needs the Roslyn C# compiler,
  echo C# 6 or later, that Visual Studio provides.
  echo Install Visual Studio, or open LunchHelper.csproj and press F6.
  exit /b 1
)

echo Using MSBuild: %BUILD%
REM -restore 首次会从 nuget.org 下载 Microsoft.Web.WebView2 包（含 WebView2Loader.dll），
REM 生成 obj\*.nuget.g.targets 并注入程序集引用；之后构建可离线。
"%BUILD%" LunchHelper.csproj -restore /p:Configuration=Release /p:Platform=AnyCPU %*
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo.
echo BUILD OK -^> bin\Release\LunchHelper.exe

REM 构建后打包便携 zip（exe + WebView2 DLL + plugins + 文档），供本地直接生成发布包
echo Packing portable ZIP...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%\pack.ps1" -ReleaseDir "%SCRIPT_DIR%\bin\Release" -Version dev -OutDir "%SCRIPT_DIR%"
if errorlevel 1 (
  echo.
  echo PACK FAILED
  exit /b 1
)

echo.
echo DONE -^> LunchHelper-dev.zip
