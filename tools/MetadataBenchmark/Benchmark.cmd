@echo off
setlocal DisableDelayedExpansion
set "ROOT=%~dp0"
set "WORK=%LOCALAPPDATA%\Temp\PCasaBenchmark-%RANDOM%-%RANDOM%"
if exist "%WORK%" goto fail
mkdir "%WORK%" || goto fail
robocopy "%ROOT%Runtime" "%WORK%\Runtime" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto fail
"%WORK%\Runtime\PCasa.MetadataBenchmark.exe" "%ROOT%."
set "RC=%ERRORLEVEL%"
rmdir /S /Q "%WORK%"
echo.
echo Bericht: "%ROOT%Benchmark-Ergebnis.txt"
echo Exit code: %RC%
pause
exit /b %RC%
:fail
echo Benchmark konnte nicht gestartet werden.
if exist "%WORK%\Runtime" rmdir /S /Q "%WORK%"
pause
exit /b 1
