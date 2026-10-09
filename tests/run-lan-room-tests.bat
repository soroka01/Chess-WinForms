@echo off
rem Compiles the LAN room transport (src\LanRoomTransport.cs, RoomTransport.cs, NetLink.cs) with tests\LanRoomTests.cs and runs it over loopback.
setlocal
cd /d "%~dp0\.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist tests\bin mkdir tests\bin
"%CSC%" /nologo /optimize+ /target:exe /main:LanRoomTests /out:tests\bin\LanRoomTests.exe ^
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    tests\LanRoomTests.cs src\LanRoomTransport.cs src\RoomTransport.cs src\NetLink.cs
if errorlevel 1 exit /b 1
tests\bin\LanRoomTests.exe
