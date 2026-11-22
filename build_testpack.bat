@echo off
REM Builds the runner into TestPack\bin. Needs the .NET 8 SDK.
cd /d "%~dp0"
dotnet publish ApiTestRunner\ApiTestRunner.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o TestPack\bin
echo.
echo Done. Hand the TestPack folder to the SAs.
pause
