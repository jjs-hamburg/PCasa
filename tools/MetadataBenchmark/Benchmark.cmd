@echo off
setlocal DisableDelayedExpansion
if not exist "%~dp0Runtime\PCasa.MetadataBenchmark.exe" goto fail
"%~dp0Runtime\PCasa.MetadataBenchmark.exe" %*
set "RC=%ERRORLEVEL%"
echo.
echo Ergebnisberichte: "%~dp0Ergebnisse"
echo Exit code: %RC%
pause
exit /b %RC%
:fail
echo Bitte das gesamte ZIP lokal entpacken.
pause
exit /b 1
