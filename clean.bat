@echo off
chcp 65001 >nul
echo ============================================
echo   Daen Launcher 清理编译产物脚本
echo ============================================

cd /d "%~dp0"

echo 清理 bin、obj、build 目录...
if exist "DaenLauncher\bin" rmdir /s /q "DaenLauncher\bin"
if exist "DaenLauncher\obj" rmdir /s /q "DaenLauncher\obj"
if exist "build" rmdir /s /q "build"
if exist ".artifacts" rmdir /s /q ".artifacts"

echo 清理完成！
pause
