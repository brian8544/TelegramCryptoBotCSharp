echo off
cls
dotnet publish -c Release -r win-x64 --self-contained
exit