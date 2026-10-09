@echo off
rem Builds the whole game plus tests\NetTests.cs (two windows play each other over loopback) and runs it.
setlocal
cd /d "%~dp0\.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist tests\bin mkdir tests\bin
"%CSC%" /nologo /optimize+ /target:exe /main:NetTests /out:tests\bin\NetTests.exe ^
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    tests\NetTests.cs tests\FakeLobby.cs tests\FakeAudio.cs src\*.cs
if errorlevel 1 exit /b 1
tests\bin\NetTests.exe %*
