@echo off
echo Copying DLL to Farthest Frontier Mods folder...
copy /Y "%1%2.dll" "G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\Mods\ResourceRelocationButton.dll"
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Copy failed!
    exit /b 1
)
echo Done! Launching Farthest Frontier...
start steam://rungameid/1044720