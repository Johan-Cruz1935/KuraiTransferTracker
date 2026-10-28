@echo off
chcp 65001 >nul
title Inicializar Base de Datos y Usuarios SQL - KuraiTransferTracker

:: Verificar permisos de Administrador y solicitar elevacion si es necesario
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Solicitando permisos de Administrador...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
set SQL_SCRIPT=%~dp0Scripts\CrearBaseDeDatos_KuraiTrackingDB.sql

echo ============================================================================
echo   PREPARACION DE BASE DE DATOS Y USUARIOS SQL PARA PRODUCCION
echo ============================================================================
echo.
echo [1/3] Ejecutando script SQL: "%SQL_SCRIPT%"
echo       (Crea KuraiTrackingDB, sus 3 tablas, habilita Modo Mixto y crea usuarios)...
sqlcmd -S localhost -C -i "%SQL_SCRIPT%"
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Hubo un problema al ejecutar el script SQL en localhost.
    pause
    exit /b 1
)

echo.
echo [2/3] Reiniciando el servicio SQL Server (MSSQLSERVER) para aplicar Modo Mixto...
powershell -Command "Restart-Service -Name 'MSSQLSERVER' -Force"
timeout /t 4 /nobreak >nul

echo.
echo [3/3] Verificando acceso de los usuarios SQL configurados...
sqlcmd -S localhost -U usr_kurai_readonly -P "KuraiReadOnly#2026!" -d KURAI2026 -C -Q "SELECT 'OK - Lectura KURAI2026' AS Estado, SUSER_SNAME() AS Usuario, DB_NAME() AS BaseDatos;"
sqlcmd -S localhost -U usr_kurai_tracking_admin -P "KuraiTrackingAdmin#2026!" -d KuraiTrackingDB -C -Q "SELECT 'OK - Admin KuraiTrackingDB' AS Estado, SUSER_SNAME() AS Usuario, DB_NAME() AS BaseDatos;"

echo.
echo ============================================================================
echo   Base de datos y usuarios listos para produccion.
echo ============================================================================
pause
