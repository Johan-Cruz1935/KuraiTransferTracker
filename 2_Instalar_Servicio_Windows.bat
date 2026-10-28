@echo off
chcp 65001 >nul
title Instalar Servicio Windows - KuraiTransferTracker

:: Verificar permisos de Administrador y solicitar elevacion si es necesario
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Solicitando permisos de Administrador...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
set SERVICE_NAME=KuraiTransferTracker
set DISPLAY_NAME=Kurai - Agente de Traqueo de Traspasos
set EXE_PATH=%~dp0KuraiTransferTracker.exe

echo ============================================================================
echo   INSTALADOR DEL SERVICIO DE WINDOWS: %SERVICE_NAME%
echo ============================================================================
echo.
echo Ruta del ejecutable: "%EXE_PATH%"
echo.

if not exist "%EXE_PATH%" (
    echo [ERROR] No se encontro KuraiTransferTracker.exe en esta carpeta.
    pause
    exit /b 1
)

:: Si el servicio ya existia previamente, detenerlo y eliminarlo antes de reinstalar
sc.exe query "%SERVICE_NAME%" >nul 2>&1
if %errorlevel% equ 0 (
    echo El servicio ya existe. Deteniendo version anterior...
    sc.exe stop "%SERVICE_NAME%" >nul 2>&1
    timeout /t 3 /nobreak >nul
    sc.exe delete "%SERVICE_NAME%" >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo [1/4] Registrando el servicio en Windows (Inicio Automatico)...
sc.exe create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" DisplayName= "%DISPLAY_NAME%" start= auto
if %errorlevel% neq 0 (
    echo [ERROR] No se pudo crear el servicio.
    pause
    exit /b 1
)

echo [2/4] Agregando descripcion del servicio...
sc.exe description "%SERVICE_NAME%" "Agente en segundo plano que respalda traspasos de KURAI2026 en KuraiTrackingDB y sincroniza con la API central."

echo [3/4] Configurando recuperacion automatica ante fallos...
sc.exe failure "%SERVICE_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000

echo [4/4] Iniciando el servicio...
sc.exe start "%SERVICE_NAME%"

echo.
echo ============================================================================
echo   Estado actual del servicio:
echo ============================================================================
sc.exe query "%SERVICE_NAME%"
echo.
echo Instalacion completada correctamente.
pause
