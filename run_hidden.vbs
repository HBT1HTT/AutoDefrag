' =====================================================================
'  run_hidden.vbs - AutoDefrag Tray v2.0 silent launcher
' ---------------------------------------------------------------------
'  Purpose:
'    Launch app.ps1 in a HIDDEN PowerShell window, without waiting.
'    Used by the HKCU\...\Run key so the tray starts on boot with no
'    black console window flash.
'  Notes:
'    - ASCII only on purpose: avoids any VBS script encoding pitfalls.
'    - "0" = hidden window, "False" = do not wait for the process.
' =====================================================================
Option Explicit

Dim shell, fso, scriptDir, appPath, cmd
Set shell = CreateObject("WScript.Shell")
Set fso   = CreateObject("Scripting.FileSystemObject")

' Resolve app.ps1 next to this VBS file, so the whole folder can be moved.
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
appPath   = fso.BuildPath(scriptDir, "app.ps1")

' -LaunchedByVbs: tells app.ps1 it was started by this launcher (i.e. the Run key
' autostart path). app.ps1 writes that verdict into Logs\app.log, so autostart
' can be verified from the log alone (the parent wscript.exe exits immediately,
' so app.ps1 cannot detect it via the parent process id).
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & appPath & """ -LaunchedByVbs"

shell.Run cmd, 0, False
