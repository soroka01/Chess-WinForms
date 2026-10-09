@echo off
rem Compiles src\Voice.cs (it needs no other file) together with tests\VoiceTests.cs and runs the checks.
rem Fake devices only; real playback gets silence and the microphone is never opened.
setlocal
cd /d "%~dp0\.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist tests\bin mkdir tests\bin
"%CSC%" /nologo /optimize+ /target:exe /main:VoiceTests /out:tests\bin\VoiceTests.exe /reference:System.dll /reference:System.Core.dll ^
    tests\VoiceTests.cs src\Voice.cs
if errorlevel 1 exit /b 1
tests\bin\VoiceTests.exe
