' Keeps the wallpaper alive.
' Silent by design: an error dialog here would steal focus from whatever is in front,
' which can make a fullscreen game drop out. Stay quiet when a game is running.

Option Explicit

Dim fso, sh, toolsDir, exe, svc, games, i, gameRunning, existing

Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

toolsDir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.GetParentFolderName(toolsDir) & "\bin\Release\net9.0-windows\HalofluxDesk.exe"

If Not fso.FileExists(exe) Then WScript.Quit 0

Set svc = GetObject("winmgmts:\\.\root\cimv2")

games = Array("ZenlessZoneZero.exe", "GenshinImpact.exe", "YuanShen.exe", "StarRail.exe")
gameRunning = False

For i = 0 To UBound(games)
    If svc.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name = '" & games(i) & "'").Count > 0 Then
        gameRunning = True
    End If
Next

If gameRunning Then WScript.Quit 0

Set existing = svc.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name = 'HalofluxDesk.exe'")

If existing.Count = 0 Then
    sh.Run """" & exe & """ --wall", 0, False
End If
