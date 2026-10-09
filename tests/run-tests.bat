@echo off
rem Compiles the engine (without any UI) together with tests\Tests.cs and runs the checks.
setlocal
cd /d "%~dp0\.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist tests\bin mkdir tests\bin
"%CSC%" /nologo /optimize+ /out:tests\bin\Tests.exe /reference:System.dll /reference:System.Core.dll ^
    tests\Tests.cs src\Position.cs src\Game.cs src\Engine.cs
if errorlevel 1 exit /b 1
tests\bin\Tests.exe
