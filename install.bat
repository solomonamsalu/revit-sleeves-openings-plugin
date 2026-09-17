@echo off
REM Install script for SleevesOpenings add-in.
REM Installs every Revit version that was built (bin\Release\<year>\) and is installed on this machine.
setlocal
set SOURCE_DIR=%~dp0
set ANY=0

for %%V in (2024 2026) do (
    if exist "%SOURCE_DIR%bin\Release\%%V\SleevesOpenings.dll" (
        if exist "%ProgramData%\Autodesk\Revit\Addins\%%V" (
            echo Installing for Revit %%V ...
            if not exist "%ProgramData%\Autodesk\Revit\Addins\%%V\SleevesOpenings" mkdir "%ProgramData%\Autodesk\Revit\Addins\%%V\SleevesOpenings"
            xcopy "%SOURCE_DIR%bin\Release\%%V\*.*" "%ProgramData%\Autodesk\Revit\Addins\%%V\SleevesOpenings\" /S /Y /Q
            copy "%SOURCE_DIR%SleevesOpenings.addin" "%ProgramData%\Autodesk\Revit\Addins\%%V\" /Y >nul
            set ANY=1
        ) else (
            echo Revit %%V is not installed - skipping.
        )
    ) else (
        echo No build for Revit %%V in bin\Release\%%V - run "dotnet build -c Release" first.
    )
)

echo.
if "%ANY%"=="1" (echo Installation complete. Restart Revit.) else (echo Nothing installed.)
pause
