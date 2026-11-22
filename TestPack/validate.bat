@echo off
REM Checks TestCases.xlsx for mistakes without calling any API.
cd /d "%~dp0"
set RUNNER=dotnet "%~dp0bin\ApiTestRunner.dll"
if exist "%~dp0bin\ApiTestRunner.exe" set RUNNER="%~dp0bin\ApiTestRunner.exe"
%RUNNER% validate --file "%~dp0TestCases.xlsx"
