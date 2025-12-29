Imports System.Windows
Imports System.Threading
Imports System.Diagnostics

Namespace SmartLockerKiosk
    Partial Public Class App
        Inherits Application
        Private Shared _mutex As Mutex

        Protected Overrides Sub OnStartup(e As StartupEventArgs)
            ' Enforce single-instance (prevents COM-port contention and double-start weirdness)
            Dim createdNew As Boolean
            _mutex = New Mutex(True, "Global\SmartLockerKiosk_SingleInstance", createdNew)

            If Not createdNew Then
                MessageBox.Show("SmartLockerKiosk is already running.")
                Shutdown()
                Return
            End If

            MyBase.OnStartup(e)
            ' Initialize local SQLite database
            DatabaseBootstrapper.InitializeDatabase()
        End Sub

        Protected Overrides Sub OnExit(e As ExitEventArgs)
            Try
                _mutex?.ReleaseMutex()
            Catch
                ' ignore
            Finally
                _mutex = Nothing
            End Try

            MyBase.OnExit(e)
        End Sub

    End Class
End Namespace


