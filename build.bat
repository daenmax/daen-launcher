@echo off
chcp 65001 >nul
echo ============================================
echo   Daen Launcher 编译发布脚本
echo ============================================

cd /d "%~dp0"

echo.
echo [1/3] 还原 NuGet 依赖...
dotnet restore DaenLauncher\DaenLauncher.csproj
if %errorlevel% neq 0 (
    echo 还原依赖失败！
    pause
    exit /b 1
)

echo.
echo [2/3] 发布单文件 exe（自包含，无需用户安装运行时）...
dotnet publish DaenLauncher\DaenLauncher.csproj -c Release -r win-x64 --self-contained true ^
  -p:Platform=x64 ^
  -p:WindowsPackageType=None ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
if %errorlevel% neq 0 (
    echo 编译失败！
    pause
    exit /b 1
)

echo.
echo [3/3] 复制产物到 build\ 目录...
if not exist build mkdir build
copy /y "DaenLauncher\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\publish\DaenLauncher.exe" "build\DaenLauncher.exe" >nul
if %errorlevel% neq 0 (
    echo 复制产物失败！
    pause
    exit /b 1
)

echo.
echo ============================================
echo   编译完成！产物位置：build\DaenLauncher.exe
echo ============================================
pause
