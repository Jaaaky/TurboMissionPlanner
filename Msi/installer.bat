

for /f %%f in ('dir /a-d /b ..\bin\Release\net472\plugins') do if exist ..\bin\Release\net472\%%f del ..\bin\Release\net472\plugins\%%f

powershell -command "cd..; ls plugins -recurse | ForEach-Object { if (Test-Path ($_.fullname -replace '\\plugins\\','\') -PathType Leaf) { $_.fullname }} | ForEach-Object { del $_ }"

.\net472\wix.exe ..\bin\release\net472\

pause

