@echo off
chcp 65001 >nul
title Desinstalar Servicio Windows - KuraiTransferTracker

:: Verificar permisos de Administrador y solicitar elevacion si es necesario
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Solicitando permisos de Administrador...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
set SERVICE_NAME=KuraiTransferTracker

echo ============================================================================
echo   DESINSTALADOR DEL SERVICIO DE WINDOWS: %SERVICE_NAME%
echo ============================================================================
echo.

sc.exe query "%SERVICE_NAME%" >nul 2>&1
if %errorlevel% neq 0 (
    echo El servicio "%SERVICE_NAME%" no se encuentra instalado en este equipo.
    pause
    exit /b 0
)

echo [1/2] Deteniendo el servicio "%SERVICE_NAME%"...
sc.exe stop "%SERVICE_NAME%"
timeout /t 3 /nobreak >nul

echo [2/2] Eliminando el servicio "%SERVICE_NAME%" del registro de Windows...
sc.exe delete "%SERVICE_NAME%"

echo.
echo ============================================================================
echo   El servicio "%SERVICE_NAME%" ha sido detenido y desinstalado correctamente.
echo   (Los datos respaldados en la base de datos KuraiTrackingDB se conservan intactos).
echo ============================================================================
pause
