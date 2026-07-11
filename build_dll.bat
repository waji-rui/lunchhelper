@echo off
chcp 936 >nul
REM ============================================================
REM  LunchHelper DLL Compile Script
REM 
REM  Supports two compilers:
REM    1. MSVC (cl.exe) - Visual Studio Build Tools required
REM    2. MinGW (gcc.exe) - MinGW-w64 required
REM 
REM  Usage:
REM    build_dll.bat msvc    -> use MSVC
REM    build_dll.bat mingw   -> use MinGW
REM    build_dll.bat         -> auto detect
REM ============================================================

setlocal enabledelayedexpansion

echo.
echo ============================================
echo   LunchHelper - DLL Compile Script
echo ============================================
echo.

set "SRC=uiaccess_helper.c"
set "OUT=uiaccess_helper.dll"

if not exist "%SRC%" (
    echo [ERROR] Source file not found: %SRC%
    echo Please run this script in the project root directory
    pause
    exit /b 1
)

set "COMPILER=%~1"

REM ============================================================
REM Auto detect compiler
REM ============================================================
if "%COMPILER%"=="" (
    echo [INFO] Detecting compiler...
    
    REM First check if cl.exe is already in PATH (e.g. from Developer Command Prompt)
    where cl.exe >nul 2>&1
    if !ERRORLEVEL! EQU 0 (
        set "COMPILER=msvc"
        echo [INFO] MSVC detected (cl.exe in PATH)
    ) else (
        REM cl.exe not in PATH; check for Visual Studio installation by looking for vcvars
        set "VCVARS_DETECTED=0"
        for %%v in (2022 2019 2017) do (
            if exist "C:\Program Files\Microsoft Visual Studio\%%v\BuildTools\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS_DETECTED=1"
            if exist "C:\Program Files\Microsoft Visual Studio\%%v\Community\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS_DETECTED=1"
            if exist "C:\Program Files\Microsoft Visual Studio\%%v\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS_DETECTED=1"
            if exist "C:\Program Files\Microsoft Visual Studio\%%v\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS_DETECTED=1"
        )
        if "!VCVARS_DETECTED!"=="1" (
            set "COMPILER=msvc"
            echo [INFO] MSVC detected (VS installation found, vcvars will be activated during build)
        ) else (
            where gcc.exe >nul 2>&1
            if !ERRORLEVEL! EQU 0 (
                set "COMPILER=mingw"
                echo [INFO] MinGW detected (gcc.exe)
            ) else (
                echo [ERROR] No compiler detected!
                echo Please install one of the following:
                echo   1. Visual Studio Build Tools (https://visualstudio.microsoft.com/downloads/)
                echo      Then run: "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
                echo   2. MinGW-w64 (https://www.mingw-w64.org/)
                echo      Add its bin directory to PATH after installation
                pause
                exit /b 1
            )
        )
    )
)

REM ============================================================
REM MSVC build
REM ============================================================
if /i "%COMPILER%"=="msvc" (
    echo.
    echo [INFO] Using MSVC compiler...
    
    REM Try to activate MSVC environment
    set "VCVARS_FOUND=0"
    
    for %%v in (2022 2019 2017) do (
        if exist "C:\Program Files\Microsoft Visual Studio\%%v\BuildTools\VC\Auxiliary\Build\vcvars64.bat" (
            call "C:\Program Files\Microsoft Visual Studio\%%v\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
            set "VCVARS_FOUND=1"
            echo [INFO] VS %%v Build Tools environment activated
            goto :msvc_compile
        )
        if exist "C:\Program Files\Microsoft Visual Studio\%%v\Community\VC\Auxiliary\Build\vcvars64.bat" (
            call "C:\Program Files\Microsoft Visual Studio\%%v\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
            set "VCVARS_FOUND=1"
            echo [INFO] VS %%v Community environment activated
            goto :msvc_compile
        )
        if exist "C:\Program Files\Microsoft Visual Studio\%%v\Professional\VC\Auxiliary\Build\vcvars64.bat" (
            call "C:\Program Files\Microsoft Visual Studio\%%v\Professional\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
            set "VCVARS_FOUND=1"
            echo [INFO] VS %%v Professional environment activated
            goto :msvc_compile
        )
        if exist "C:\Program Files\Microsoft Visual Studio\%%v\Enterprise\VC\Auxiliary\Build\vcvars64.bat" (
            call "C:\Program Files\Microsoft Visual Studio\%%v\Enterprise\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
            set "VCVARS_FOUND=1"
            echo [INFO] VS %%v Enterprise environment activated
            goto :msvc_compile
        )
    )
    
    :msvc_compile
    if "!VCVARS_FOUND!"=="0" (
        echo [WARN] Cannot auto-activate MSVC environment, trying cl.exe directly
        echo If compilation fails, please run vcvars64.bat manually
    )
    
    echo [BUILD] cl /LD /O2 /MT %SRC% /Fe:%OUT% /link user32.lib advapi32.lib shell32.lib comctl32.lib gdi32.lib
    cl /LD /O2 /MT %SRC% /Fe:%OUT% /link user32.lib advapi32.lib shell32.lib comctl32.lib gdi32.lib
    
    if !ERRORLEVEL! EQU 0 (
        if exist "%OUT%" (
            echo.
            echo ============================================
            echo   Build successful!
            echo   Output: %OUT%
            for %%A in ("%OUT%") do echo   Size: %%~zA bytes
            echo ============================================
        )
    ) else (
        echo.
        echo [ERROR] Build failed! Please check MSVC installation
        exit /b 1
    )
    goto :done
)

REM ============================================================
REM MinGW build
REM ============================================================
if /i "%COMPILER%"=="mingw" (
    echo.
    echo [INFO] Using MinGW compiler...
    echo [BUILD] gcc -shared -O2 -static -o %OUT% %SRC% -luser32 -ladvapi32 -lshell32 -lcomctl32 -lgdi32
    
    gcc -shared -O2 -static -o %OUT% %SRC% -luser32 -ladvapi32 -lshell32 -lcomctl32 -lgdi32
    
    if !ERRORLEVEL! EQU 0 (
        if exist "%OUT%" (
            echo.
            echo ============================================
            echo   Build successful!
            echo   Output: %OUT%
            for %%A in ("%OUT%") do echo   Size: %%~zA bytes
            echo ============================================
        )
    ) else (
        echo.
        echo [ERROR] Build failed! Please check MinGW-w64 installation
        exit /b 1
    )
    goto :done
)

:done
echo.
echo [TIP] After DLL build, run build_exe.bat to package the program
echo.
pause
endlocal
