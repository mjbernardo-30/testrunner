@echo off
REM Lists every field returned by a scenario so you can copy Field Paths into Assertions / Capture.
cd /d "%~dp0"
set RUNNER=dotnet "%~dp0bin\ApiTestRunner.dll"
if exist "%~dp0bin\ApiTestRunner.exe" set RUNNER="%~dp0bin\ApiTestRunner.exe"
set /p SCN=Scenario ID to discover (e.g. BUY-EXISTING): 
set /p ENVNAME=Environment (press Enter for default): 
set ENVARG=
if not "%ENVNAME%"=="" set ENVARG=--env %ENVNAME%
%RUNNER% discover --file "%~dp0TestCases.xlsx" --scenario %SCN% %ENVARG% --open
