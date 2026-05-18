@echo off
echo === Building v1.2.38 MSI ===
cd /d C:\Users\hp\porta-agent-windows-x64
powershell -ExecutionPolicy Bypass -Command "& 'scripts\build-msi'" > src\Porta.ControlCenter\build-1238-output.txt 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED >> src\Porta.ControlCenter\build-1238-output.txt
    pause
    exit /b 1
)
echo === Installing === >> src\Porta.ControlCenter\build-1238-output.txt
powershell -ExecutionPolicy Bypass -Command "& 'scripts\install'" >> src\Porta.ControlCenter\build-1238-output.txt 2>&1
echo === Git commit and push === >> src\Porta.ControlCenter\build-1238-output.txt
git add -A >> src\Porta.ControlCenter\build-1238-output.txt 2>&1
git commit -m "v1.2.38: Fix overload in week/2-week calendar, wrong initial focus value, content left-shift" >> src\Porta.ControlCenter\build-1238-output.txt 2>&1
git push >> src\Porta.ControlCenter\build-1238-output.txt 2>&1
echo DONE at %TIME% >> src\Porta.ControlCenter\build-1238-output.txt
