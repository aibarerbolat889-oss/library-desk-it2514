@echo off
setlocal
pushd "%~dp0"
if not exist "..\runtime\win-x64" mkdir "..\runtime\win-x64"
cl /nologo /W4 /WX /O2 /TC /LD /MT fine.c /link /MACHINE:X64 /OUT:"..\runtime\win-x64\libraryfine.dll" /IMPLIB:fine.lib
set "build_result=%errorlevel%"
popd
exit /b %build_result%
