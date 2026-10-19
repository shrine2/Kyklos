@echo off
rem Double-click wrapper so the installer never needs an execution-policy decision:
rem the .ps1 beside it does the work, this only lifts the policy for this one call.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
pause
