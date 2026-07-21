@echo off
REM Launch BubblesBot from this script's directory regardless of where it's invoked from.
cd /d "%~dp0"
dotnet run --project src/BubblesBot.Bot %*
