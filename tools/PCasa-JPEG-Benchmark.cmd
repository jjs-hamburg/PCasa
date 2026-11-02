@echo off
rem Testet nur eine gewaehlte JPG-Datei. Das Original wird nicht veraendert.
cd /d "%~dp0"
start /wait "" "%~dp0PCasa.App.exe" --jpeg-benchmark
