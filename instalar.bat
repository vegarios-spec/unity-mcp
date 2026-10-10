@echo off
chcp 65001 >nul
title Instalar Unity MCP
cd /d "%~dp0"
echo === Instalando el MCP propio de Unity ===
set "PYBASE=%LOCALAPPDATA%\Python\pythoncore-3.14-64\python.exe"
if not exist "%PYBASE%" set "PYBASE=py"
if not exist ".venv\Scripts\python.exe" (
  echo Creando entorno de Python...
  "%PYBASE%" -m venv .venv || goto :error
)
echo Instalando paquetes (mcp)...
".venv\Scripts\python.exe" -m pip install -q --upgrade pip
".venv\Scripts\python.exe" -m pip install -q "mcp<2" || goto :error
echo Probando que el servidor carga...
".venv\Scripts\python.exe" -c "import server; print('Herramientas:', len(server.mcp._tool_manager.list_tools()))" || goto :error
echo Registrando en Claude Desktop y Claude Code...
".venv\Scripts\python.exe" registrar.py || goto :error
echo.
echo LISTO. Cierra por completo la app de Claude (icono junto al reloj ^> Quit) y vuelve a abrirla.
pause
exit /b 0
:error
echo.
echo *** Algo fallo. Mandale a Claude una captura de esta ventana. ***
pause
exit /b 1
