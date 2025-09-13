@echo off
setlocal ENABLEDELAYEDEXPANSION

rem ==============================================
rem BookFlow NT8 Deployment Script
rem Usage: deploy.bat [Configuration]
rem Default configuration: Debug
rem Copies NT8DataEngine + BookFlow.Shared into NinjaTrader Custom folder.
rem ==============================================

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

set SCRIPT_DIR=%~dp0
rem Remove trailing backslash if present
if "%SCRIPT_DIR:~-1%"=="\" set SCRIPT_DIR=%SCRIPT_DIR:~0,-1%
set ROOT=%SCRIPT_DIR%\..

set NT8_OUT=%ROOT%\NT8DataEngine\bin\%CONFIG%\net48
set SHARED_OUT=%ROOT%\SharedLibrary.Standard\bin\%CONFIG%\netstandard2.0
set TARGET_DIR=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom

cls
echo =============================================
echo   BookFlow NT8 Deployment
echo   Configuration: %CONFIG%
echo   Source (NT8):  %NT8_OUT%
echo   Source (SHR):  %SHARED_OUT%
echo   Target:        %TARGET_DIR%
echo ==============================================

if not exist "%TARGET_DIR%" (
  echo ERROR: Target NinjaTrader Custom folder not found.
  echo Ensure NinjaTrader 8 is installed and has run at least once.
  exit /b 1
)

echo Building solution (configuration %CONFIG%)...
call dotnet build "%ROOT%\NT8DataEngine\BookFlow.NT8DataEngine.csproj" -c %CONFIG% -v m || (echo Build failed & exit /b 1)

echo.
echo Copying assemblies...
for %%A in (NT8DataEngine.dll NT8DataEngine.pdb) do (
  if exist "%NT8_OUT%\%%A" (
    echo   Copy %%A
    copy /Y "%NT8_OUT%\%%A" "%TARGET_DIR%\" >nul || (echo     FAILED copying %%A & exit /b 1)
  )
)
for %%A in (BookFlow.Shared.dll BookFlow.Shared.pdb) do (
  if exist "%SHARED_OUT%\%%A" (
    echo   Copy %%A
    copy /Y "%SHARED_OUT%\%%A" "%TARGET_DIR%\" >nul || (echo     FAILED copying %%A & exit /b 1)
  ) else (
    rem In case the shared dll was copied to the NT8 output by mismatch (should not normally)
    if exist "%NT8_OUT%\%%A" (
      echo   Copy %%A (from NT8 output)
      copy /Y "%NT8_OUT%\%%A" "%TARGET_DIR%\" >nul || (echo     FAILED copying %%A & exit /b 1)
    )
  )
)

echo.
echo Verifying deployed files...
for %%A in (NT8DataEngine.dll BookFlow.Shared.dll) do (
  if not exist "%TARGET_DIR%\%%A" (
    echo   MISSING: %%A (deployment incomplete)
    set DEPLOY_ERROR=1
  ) else (
    for %%F in ("%TARGET_DIR%\%%A") do echo   Present: %%~nF (%%~zF bytes)
  )
)
if defined DEPLOY_ERROR (
  echo Deployment finished with errors.
  exit /b 1
)

echo.
echo Deployment completed successfully.
echo If NinjaTrader 8 was running, restart it to load updated assemblies.
endlocal
pause