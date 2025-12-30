Imports System.Windows
Imports System.Threading
Imports System.Diagnostics

Namespace SmartLockerKiosk
    Partial Public Class App
        Inherits Application
        Private Shared _mutex As Mutex
        Public Sub New()
            InitializeComponent()
        End Sub
        Protected Overrides Sub OnStartup(e As StartupEventArgs)

            ' Enforce single-instance (prevents COM-port contention and double-start weirdness)
            Dim createdNew As Boolean
            _mutex = New Mutex(True, "Global\SmartLockerKiosk_SingleInstance", createdNew)

            If Not createdNew Then
                ' Best-effort audit (may not yet be initialized)
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.SystemStartup,
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "App",
                    .Outcome = Audit.AuditOutcome.Denied,
                    .CorrelationId = Guid.NewGuid().ToString("N"),
                    .ReasonCode = "SingleInstanceAlreadyRunning"
                })

                MessageBox.Show("SmartLockerKiosk is already running.")
                Shutdown()
                Return
            End If

            MyBase.OnStartup(e)

            ' Initialize audit first so subsequent startup actions can be recorded
            Audit.AuditServices.Initialize()

            Dim correlationId As String = Guid.NewGuid().ToString("N")

            ' Record app startup (separate from logger initialization)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.SystemStartup,
                .ActorType = Audit.ActorType.System,
                .ActorId = "System:SmartLockerKiosk",
                .AffectedComponent = "App",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId
            })

            ' Initialize local SQLite database (audit success/failure without leaking details)
            Try
                DatabaseBootstrapper.InitializeDatabase()

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' using existing enum; consider adding DatabaseInitialized later
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "DatabaseBootstrapper",
                    .Outcome = Audit.AuditOutcome.Success,
                    .CorrelationId = correlationId,
                    .ReasonCode = "DatabaseInitialized"
                })

            Catch ex As Exception
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.AuditLogFailure, ' best available with current enum set
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "DatabaseBootstrapper",
                    .Outcome = Audit.AuditOutcome.Error,
                    .CorrelationId = correlationId,
                    .ReasonCode = "DatabaseInitFailed"
                })

                Throw
            End Try

        End Sub
        Protected Overrides Sub OnExit(e As ExitEventArgs)

            Dim correlationId As String = Guid.NewGuid().ToString("N")

            ' Log shutdown BEFORE releasing shared resources (best-effort)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.SystemShutdown,
                .ActorType = Audit.ActorType.System,
                .ActorId = "System:SmartLockerKiosk",
                .AffectedComponent = "App",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId
            })

            Try
                _mutex?.ReleaseMutex()
            Catch
                ' Ignore, but record that cleanup was imperfect (non-fatal)
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.SystemShutdown,
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "Mutex",
                    .Outcome = Audit.AuditOutcome.Error,
                    .CorrelationId = correlationId,
                    .ReasonCode = "MutexReleaseFailed"
                })
            Finally
                _mutex = Nothing
            End Try

            MyBase.OnExit(e)

        End Sub
    End Class
End Namespace


