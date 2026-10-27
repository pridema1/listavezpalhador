@echo off
REM Compila o ListaDaVez.exe usando o compilador C# que já vem no Windows
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" /nologo /optimize /target:exe /out:dist\ListaDaVez.exe /r:System.Web.Extensions.dll /resource:src\index.html,index.html src\Servidor.cs
if errorlevel 1 (echo ERRO na compilacao & pause & exit /b 1)
echo OK: dist\ListaDaVez.exe
