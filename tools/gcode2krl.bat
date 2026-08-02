@echo off
REM gcode2krl.bat — G-code to KRL converter wrapper for OrcaSlicer post-processor
REM Called by OrcaSlicer with the G-code file path as the first argument
REM Standalone .exe — no Node.js installation required
REM v3.2: added crash dump for exit codes >= 128

set LOGFILE=%~dp0gcode2krl_error.log
set EXIT_CODE=0

echo [%date% %time%][DIAG] CWD=%CD% >> "%LOGFILE%"

"%~dp0gcode2krl.exe" --split --start-config=%~dp0start_position.json %*
set EXIT_CODE=%ERRORLEVEL%

if %EXIT_CODE% GTR 0 (
    echo [%date% %time%] gcode2krl exited with code %EXIT_CODE% >> "%LOGFILE%"
    echo   Args: %* >> "%LOGFILE%"
    echo   CWD: %CD% >> "%LOGFILE%"
)

exit /b %EXIT_CODE%
