@echo off
chcp 936 >nul
REM ============================================================
REM  LunchHelper EXE Package Script
REM 
REM  Uses PyInstaller to package Python program into standalone EXE
REM 
REM  Requirements:
REM    1. Python 3.13+ installed
REM    2. PyInstaller installed: pip install pyinstaller
REM    3. uiaccess_helper.dll compiled (run build_dll.bat first)
REM 
REM  Usage:
REM    build_exe.bat          -> single-file EXE
REM    build_exe.bat folder   -> folder mode (faster startup)
REM    build_exe.bat clean    -> clean build artifacts
REM ============================================================

setlocal enabledelayedexpansion

echo.
echo ============================================
echo   LunchHelper - EXE Package Script
echo ============================================
echo.

set "APP_NAME=LunchHelper"
set "MAIN_SCRIPT=lunch_helper.py"
set "DLL_NAME=uiaccess_helper.dll"
set "ICON_NAME=lunch_helper.ico"

REM Handle clean argument first, before prerequisite checks
if /i "%~1"=="clean" goto :clean

REM Check Python
where python >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Python not found! Please install Python 3.13+
    pause
    exit /b 1
)

REM Check PyInstaller
python -c "import PyInstaller" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [INFO] Installing PyInstaller...
    pip install pyinstaller
    if %ERRORLEVEL% NEQ 0 (
        echo [ERROR] PyInstaller installation failed!
        echo Please run manually: pip install pyinstaller
        pause
        exit /b 1
    )
)

REM Check main script
if not exist "%MAIN_SCRIPT%" (
    echo [ERROR] Main script not found: %MAIN_SCRIPT%
    echo Please run this script in the project root directory
    pause
    exit /b 1
)

REM Check DLL
if not exist "%DLL_NAME%" (
    echo [WARN] Not found: %DLL_NAME%
    echo Please run build_dll.bat to compile the DLL first
    echo.
    echo Continue packaging without DLL? Lock screen will use normal topmost only.
    set /p CONTINUE="Enter Y to continue, any other key to exit: "
    if /i not "!CONTINUE!"=="Y" exit /b 1
)

REM Handle command line arguments
set "BUILD_MODE=onefile"
if /i "%~1"=="folder" set "BUILD_MODE=folder"

REM Create logs directory
if not exist "logs" mkdir logs

REM Build PyInstaller arguments
set "PYI_ARGS="

REM Single-file or folder mode
if "%BUILD_MODE%"=="onefile" (
    echo [INFO] Package mode: single-file EXE
    set "PYI_ARGS=--onefile"
) else (
    echo [INFO] Package mode: folder
    set "PYI_ARGS=--onedir"
)

REM Basic options
set "PYI_ARGS=%PYI_ARGS% --name=%APP_NAME%"
set "PYI_ARGS=%PYI_ARGS% --windowed"
set "PYI_ARGS=%PYI_ARGS% --clean"
set "PYI_ARGS=%PYI_ARGS% --noconfirm"

REM Add DLL (use absolute path to avoid specpath issue)
if exist "%DLL_NAME%" (
    echo [INFO] Including DLL: %DLL_NAME%
    set "PYI_ARGS=%PYI_ARGS% --add-binary=%CD%\%DLL_NAME%;."
)

REM Add icon if exists
if exist "%ICON_NAME%" (
    echo [INFO] Including icon: %ICON_NAME%
    set "PYI_ARGS=%PYI_ARGS% --icon=%ICON_NAME%"
)

REM Hide console in non-debug mode
set "PYI_ARGS=%PYI_ARGS% --hide-console=hide-early"

REM Exclude unnecessary modules
set "PYI_ARGS=%PYI_ARGS% --exclude-module=matplotlib"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=numpy"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=pandas"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=PIL"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=PyQt5"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=PyQt6"
set "PYI_ARGS=%PYI_ARGS% --exclude-module=wx"

REM Add hidden imports
set "PYI_ARGS=%PYI_ARGS% --hidden-import=tkinter"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=logging"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=logging.handlers"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=configparser"
set "PYI_ARGS=%PYI_ARGS% --hidden-import=ctypes"

REM Output paths
set "PYI_ARGS=%PYI_ARGS% --distpath=./dist"
set "PYI_ARGS=%PYI_ARGS% --workpath=./build"
set "PYI_ARGS=%PYI_ARGS% --specpath=./build"

REM Main script
set "PYI_ARGS=%PYI_ARGS% %MAIN_SCRIPT%"

echo.
echo [INFO] Start packaging...
echo [CMD] pyinstaller %PYI_ARGS%
echo.

pyinstaller %PYI_ARGS%

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ============================================
    echo   Package successful!
    echo.
    if "%BUILD_MODE%"=="onefile" (
        echo   Output: dist\%APP_NAME%.exe
    ) else (
        echo   Output dir: dist\%APP_NAME%\
        echo   Executable: dist\%APP_NAME%\%APP_NAME%.exe
    )
    echo.
    echo   Usage:
    echo     Run directly     -> config window
    echo     %APP_NAME% -lock -> control mode
    echo     %APP_NAME% -debug -> debug mode
    echo ============================================
) else (
    echo.
    echo [ERROR] Package failed! Please check error messages
    exit /b 1
)

goto :done

:clean
echo.
echo [INFO] Cleaning build artifacts...
if exist "build" rmdir /s /q "build"
if exist "dist" rmdir /s /q "dist"
if exist "__pycache__" rmdir /s /q "__pycache__"
if exist "*.spec" del /q "*.spec"
echo [INFO] Clean complete!
goto :done

:done
echo.
pause
endlocal
