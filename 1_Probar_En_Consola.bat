@echo off
chcp 65001 >nul
title Kurai Transfer Tracker - Prueba en Consola
cd /d "%~dp0"

echo ============================================================================
echo   KURAI - AGENTE DE TRAQUEO DE TRASPASOS (MODO CONSOLA)
echo ============================================================================
echo.
echo Seleccione como desea probar el programa:
echo   [1] Ejecutar UN SOLO CICLO de sincronizacion y terminar (--run-once)
echo   [2] Ejecutar de forma CONTINUA en consola (revisa cada 30s, Ctrl+C para salir)
echo.
set /p OPCION="Ingrese 1 o 2 (Por defecto 1): "

if "%OPCION%"=="2" (
    echo.
    echo Iniciando en modo continuo... Presione Ctrl+C para detener.
    echo ----------------------------------------------------------------------------
    "%~dp0KuraiTransferTracker.exe"
) else (
    echo.
    echo Ejecutando un ciclo unico de sincronizacion (--run-once)...
    echo ----------------------------------------------------------------------------
    "%~dp0KuraiTransferTracker.exe" --run-once
)

echo.
echo ============================================================================
echo   Ejecucion finalizada.
echo ============================================================================
pause
