@echo off
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:"%~dp0DeskPin.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "%~dp0src\DeskPin.cs"
if errorlevel 1 goto failed
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%~dp0DeskPinUI.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "%~dp0src\DeskPinUI.cs"
if errorlevel 1 goto failed
echo [OK] built DeskPin.exe + DeskPinUI.exe
goto end
:nocsc
echo [ERR] csc.exe not found
exit /b 1
:failed
echo [ERR] compile failed
exit /b 1
:end
endlocal
