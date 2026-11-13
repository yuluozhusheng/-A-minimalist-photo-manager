@echo off
setlocal
pushd "%~dp0" || exit /b 1
set "PM_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%PM_CSC%" set "PM_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%PM_CSC%" (
  echo ERROR: .NET Framework compiler not found. Install .NET Framework 4.8.
  popd
  exit /b 1
)
set "PM_FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%PM_FRAMEWORK%\Microsoft.VisualBasic.dll" set "PM_FRAMEWORK=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "dist" mkdir "dist"
if not exist "dist" (
  echo ERROR: Cannot create the dist folder.
  popd
  exit /b 1
)
"%PM_CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%PM_FRAMEWORK%\Microsoft.VisualBasic.dll" /out:"dist\PhotoManager.exe" "Program.cs"
set "PM_RESULT=%ERRORLEVEL%"
if not "%PM_RESULT%"=="0" (
  echo Build failed.
  popd
  exit /b %PM_RESULT%
)
echo Build succeeded: dist\PhotoManager.exe
popd
exit /b 0
