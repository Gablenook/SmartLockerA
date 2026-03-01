Imports System.Windows

Namespace SmartLockerKiosk
    Friend Module UiKioskHelpers
        Friend Sub ApplyKioskFullScreen(w As Window)
            w.WindowStyle = WindowStyle.None
            w.ResizeMode = ResizeMode.NoResize
            w.Topmost = True

            w.WindowState = WindowState.Normal
            w.Left = 0
            w.Top = 0
            w.Width = SystemParameters.PrimaryScreenWidth
            w.Height = SystemParameters.PrimaryScreenHeight
            w.WindowState = WindowState.Maximized
        End Sub
    End Module
End Namespace