@echo off
chcp 65001 > nul
rem 윈도우에서 WPF 앱 빌드 (필요: .NET 8 SDK, Java 17+ 는 실행할 때만)
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo .NET 8 SDK 가 필요합니다: https://dotnet.microsoft.com/download/dotnet/8.0 & pause & exit /b 1)
dotnet test BinanceCumulative.Pc.Tests -c Release || (pause & exit /b 1)
dotnet publish BinanceCumulative.Pc -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist || (pause & exit /b 1)
if exist binance-cumulative-server.jar copy /y binance-cumulative-server.jar dist\ >nul
if exist ..\server\build\libs\binance-cumulative-server.jar copy /y ..\server\build\libs\binance-cumulative-server.jar dist\ >nul
echo.
echo 완료: dist\BinanceCumulative.exe
pause
