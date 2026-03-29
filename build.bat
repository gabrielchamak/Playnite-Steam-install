@echo off
echo ============================================
echo   Silent Install - Build
echo ============================================

if exist dist rmdir /s /q dist
mkdir dist
if not exist obj mkdir obj

echo [1/3] Restoring packages...
dotnet restore SilentInstall.csproj
if %errorlevel% neq 0 ( echo ERROR: Restore failed & pause & exit /b 1 )

echo [2/3] Building...
dotnet build SilentInstall.csproj -c Release --no-restore
if %errorlevel% neq 0 ( echo ERROR: Build failed & pause & exit /b 1 )

echo [3/3] Packaging...
copy "bin\Release\net462\SilentInstall.dll" dist\ > nul
copy extension.yaml dist\ > nul

echo.
echo ============================================
echo   Done! Copy dist\ contents to:
echo   %%APPDATA%%\Roaming\Playnite\Extensions\SilentInstall\
echo ============================================
pause
