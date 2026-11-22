@echo off
REM Runs all scenarios in TestCases.xlsx.
REM The window closes when finished unless Settings -> IsDebug = Y (or add --IsDebug true).
REM Optional: run_tests.bat SIT1        (environment)
REM           run_tests.bat SIT1 BUY-*  (environment + scenario filter)
cd /d "%~dp0"
set RUNNER=dotnet "%~dp0bin\ApiTestRunner.dll"
if exist "%~dp0bin\ApiTestRunner.exe" set RUNNER="%~dp0bin\ApiTestRunner.exe"
set ENVARG=
set SCNARG=
if not "%~1"=="" set ENVARG=--env %~1
if not "%~2"=="" set SCNARG=--scenario %~2
%RUNNER% run --file "%~dp0TestCases.xlsx" %ENVARG% %SCNARG% --open
