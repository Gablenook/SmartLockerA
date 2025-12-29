Imports System.Diagnostics
Imports System.IO

Public Module KeyboardHelper

    Private ReadOnly TouchKeyboardPath As String =
        "C:\Program Files\Common Files\Microsoft Shared\ink\TabTip.exe"

    Public Sub ShowTouchKeyboard()
        Try
            If File.Exists(TouchKeyboardPath) Then
                Process.Start(New ProcessStartInfo With {
                    .FileName = TouchKeyboardPath,
                    .UseShellExecute = True
                })
            End If
        Catch
            ' Intentionally ignore — kiosk should not crash over keyboard
        End Try
    End Sub

    Public Sub HideTouchKeyboard()
        For Each p In Process.GetProcessesByName("TabTip")
            Try
                p.Kill()
            Catch
            End Try
        Next
    End Sub

End Module
