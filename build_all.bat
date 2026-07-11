@echo off
chcp 936 >nul
REM ============================================================
REM  LunchHelper One-Click Build Script
REM 
REM  Automatically runs: build_dll.bat -> build_exe.bat
REM ============================================================

echo.
echo ============================================
echo   LunchHelper - One-Click Build
echo ============================================
echo.

echo [Step 1/2] Compiling C helper DLL...
call build_dll.bat
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] DLL compilation failed, build aborted
    pause
    exit /b 1
)

echo.
echo [Step 2/2] Packaging Python EXE...
call build_exe.bat
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] EXE packaging failed
    pause
    exit /b 1
)

echo.
echo ============================================
echo   Build complete!
echo   Output is in the dist directory
echo ============================================
echo.
pause
