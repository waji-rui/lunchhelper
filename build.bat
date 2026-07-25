@echo off
setlocal

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
"%BUILD%" LunchHelper.csproj /p:Configuration=Release /p:Platform=AnyCPU %*
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  exit /b 1
)

echo.
echo BUILD OK -^> bin\Release\LunchHelper.exe
echo Deploy LunchHelper.exe together with config.json in the same folder.
