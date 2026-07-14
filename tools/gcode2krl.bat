@echo off
REM gcode2krl.bat — G-code to KRL converter wrapper for OrcaSlicer post-processor
REM Called by OrcaSlicer with the G-code file path as the first argument
REM Standalone .exe — no Node.js installation required

"%~dp0gcode2krl.exe" %*
exit /b %ERRORLEVEL%
