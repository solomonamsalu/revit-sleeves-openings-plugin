@echo off
REM Install script for SleevesOpenings add-in (Revit 2024)
set SOURCE_DIR=%~dp0
set REVIT_ADDINS=%ProgramData%\Autodesk\Revit\Addins\2024

if not exist "%REVIT_ADDINS%\SleevesOpenings" mkdir "%REVIT_ADDINS%\SleevesOpenings"
xcopy "%SOURCE_DIR%bin\Release\*.*" "%REVIT_ADDINS%\SleevesOpenings\" /S /Y
copy "%SOURCE_DIR%SleevesOpenings.addin" "%REVIT_ADDINS%\" /Y

echo.
echo Installation complete. Restart Revit.
pause
