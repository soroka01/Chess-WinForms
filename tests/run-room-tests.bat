@echo off
rem Unit tests of the room layer (RoomSession) on an in-memory transport.
setlocal
cd /d "%~dp0\.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist tests\bin mkdir tests\bin
"%CSC%" /nologo /optimize+ /target:exe /main:RoomTests /out:tests\bin\RoomTests.exe ^
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    tests\RoomTests.cs tests\FakeRoom.cs src\RoomSession.cs src\RoomTransport.cs src\NetLink.cs
if errorlevel 1 exit /b 1
tests\bin\RoomTests.exe
