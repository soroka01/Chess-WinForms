@echo off
rem Builds Chess.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or .NET SDK required.
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found - .NET Framework 4.x is required.
    exit /b 1
)

set ICON=
if exist chess.ico set ICON=/win32icon:chess.ico

rem build next to the target first, so a running Chess.exe does not break the build
"%CSC%" /nologo /target:winexe /optimize+ /out:Chess.build.exe %ICON% ^
    /reference:System.dll /reference:System.Core.dll ^
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    src\*.cs
if errorlevel 1 (
    echo Build FAILED.
    exit /b 1
)

move /y Chess.build.exe Chess.exe >nul 2>&1
if errorlevel 1 (
    move /y Chess.build.exe Chess.new.exe >nul
    echo Build OK, but Chess.exe is running and cannot be replaced.
    echo The new version is %~dp0Chess.new.exe - close the game and rename it to Chess.exe.
    exit /b 0
)
echo Build OK: %~dp0Chess.exe
