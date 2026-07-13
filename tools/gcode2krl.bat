@echo off
REM gcode2krl.bat — G-code to KRL converter wrapper for OrcaSlicer post-processor
REM Called by OrcaSlicer with the G-code file path as the first argument
REM Uses Node.js to run the converter

node "%~dp0gcode2krl.js" %*
exit /b %ERRORLEVEL%
