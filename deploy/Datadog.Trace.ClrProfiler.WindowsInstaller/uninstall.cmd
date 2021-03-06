@rem Modified by SignalFx
@echo off
setlocal

echo Executing uninstall.cmd at %date% %time%

set DATADOG_APPCMD_CMDLINE=%systemroot%\system32\inetsrv\appcmd.exe set config /section:system.webServer/modules /-[name='SignalFxTracingModule']

IF EXIST %systemroot%\system32\inetsrv\appcmd.exe (
    echo Attempting to uninstall the SignalFx ASP.NET HttpModule with %systemroot%\system32\inetsrv\appcmd.exe
    %DATADOG_APPCMD_CMDLINE% 2>&1
) ELSE (
    echo "%systemroot%\system32\inetsrv\appcmd.exe" doesn't exist. The SignalFx ASP.NET HttpModule will not be uninstalled by this installer.
)

REM Always report success
exit /b 0