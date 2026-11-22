@echo off
REM ---------------------------------------------------------------------------
REM  Builds the test runner WITH SQL support (Method = SQL steps) into TestPack\bin
REM  Needs: .NET 8 SDK, and access to NuGet (or your internal package feed)
REM         for Microsoft.Data.SqlClient.
REM  Place this file next to build_testpack.bat (solution folder).
REM ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"

where dotnet >/dev/null 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found. Install the .NET 8 SDK first.
    goto :end
)

if not exist "ApiTestRunner\ApiTestRunner.csproj" (
    echo [ERROR] ApiTestRunner\ApiTestRunner.csproj not found. Put this .bat in the solution folder.
    goto :end
)

echo Building ApiTestRunner with SQL support...
echo.
dotnet publish "ApiTestRunner\ApiTestRunner.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSql=true -o "TestPack\bin"
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. If the error mentions Microsoft.Data.SqlClient or NuGet,
    echo         this PC cannot reach the package source. Check nuget.org / your internal feed.
    goto :end
)

echo.
echo Done. TestPack\bin\ApiTestRunner.exe now supports SQL steps.
echo Hand the TestPack folder to the SAs.

:end
echo.
pause
