@echo off
REM gcode2krl.bat — wrapper for C# gcode2krl.exe
REM Place next to gcode2krl.exe in the publish directory
REM v4.0 C# edition — split mode controlled by start_position.json

set LOGFILE=%~dp0gcode2krl_error.log

echo [%date% %time%] gcode2krl v4.0 called >> "%LOGFILE%"
echo   Args: %* >> "%LOGFILE%"
echo   CWD: %CD% >> "%LOGFILE%"

"%~dp0gcode2krl.exe" --start-config=%~dp0start_position.json %*

set EXIT_CODE=%ERRORLEVEL%
if %EXIT_CODE% GTR 0 (
    echo [%date% %time%] gcode2krl exited with code %EXIT_CODE% >> "%LOGFILE%"
)
exit /b %EXIT_CODE%
