@echo off
REM gcode2krl.bat — G-code to KRL converter wrapper for OrcaSlicer post-processor
REM Called by OrcaSlicer with the G-code file path as the first argument
REM Uses C# standalone exe — no Node.js, no .NET runtime required
REM v4.0: split mode now controlled by start-config.json ("split": true/false)

set LOGFILE=%~dp0gcode2krl_error.log
set EXIT_CODE=0

echo [%date% %time%][DIAG] CWD=%CD% >> "%LOGFILE%"

REM Point to the C# publish exe (12 MB) — or replace with local copy if preferred
"%~dp0GCode2Krl.Cli\publish\gcode2krl.exe" --start-config=%~dp0start_position.json %*
set EXIT_CODE=%ERRORLEVEL%

if %EXIT_CODE% GTR 0 (
    echo [%date% %time%] gcode2krl exited with code %EXIT_CODE% >> "%LOGFILE%"
    echo   Args: %* >> "%LOGFILE%"
    echo   CWD: %CD% >> "%LOGFILE%"
)

exit /b %EXIT_CODE%
