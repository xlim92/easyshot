@echo off
rem Builds build\native\EasyShot.exe with Native AOT, on Windows only. Needs the .NET 10 SDK and the C++ build tools
rem of Visual Studio (see README), with the ARM64 ones for ARM builds. "build.cmd win-arm64" builds for ARM.
set RID=%1
if "%RID%"=="" set RID=win-x64
dotnet publish "%~dp0EasyShot.csproj" -c Release -r %RID% -o "%~dp0..\build\native"
