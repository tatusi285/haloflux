' Keeps the wallpaper alive without flashing a console window.
' The previous .cmd version opened a black window every five minutes.

Dim fso, sh, toolsDir, exe, svc, list

Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

toolsDir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.GetParentFolderName(toolsDir) & "\bin\Release\net9.0-windows\HalofluxDesk.exe"

If Not fso.FileExists(exe) Then WScript.Quit 0

Set svc = GetObject("winmgmts:\\.\root\cimv2")
Set list = svc.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='HalofluxDesk.exe'")

If list.Count = 0 Then
    sh.Run """" & exe & """ --wall", 0, False
End If
