Imports System.Threading
Imports System.Windows
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Namespace SmartLockerKiosk
    Partial Public Class App

        Private _mutex As Mutex
        Public Property LockerController As LockerControllerService
        Private Const SystemActorId As String = "System:SmartLockerKiosk"
        Private Const BootstrapCommissioningActorId As String = "BOOTSTRAP:COMMISSIONING"

        Protected Overrides Sub OnStartup(e As StartupEventArgs)

            If Not TryAcquireSingleInstanceMutex() Then
                Try
                    Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.SystemStartup,
                .ActorType = Audit.ActorType.System,
                .ActorId = SystemActorId,
                .AffectedComponent = "App",
                .Outcome = Audit.AuditOutcome.Denied,
                .CorrelationId = Guid.NewGuid().ToString("N"),
                .ReasonCode = "SingleInstanceAlreadyRunning"
            })
                Catch
                End Try

                MessageBox.Show("SmartLockerKiosk is already running.")
                Shutdown()
                Return
            End If

            MyBase.OnStartup(e)

            Audit.AuditServices.Initialize()

            Dim correlationId As String = Guid.NewGuid().ToString("N")

            SafeAudit(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.SystemStartup,
        .ActorType = Audit.ActorType.System,
        .ActorId = SystemActorId,
        .AffectedComponent = "App",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = correlationId,
        .ReasonCode = "AppStarting"
    })

            ApplyRuntimeConfig(correlationId)
            InitializeLocalDatabaseOrFail(correlationId)

            Dim decision = DetermineStartupMode(correlationId)

            Dim w As Window = CreateStartupWindow(decision)
            Me.MainWindow = w
            w.Show()

            SafeAudit(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.SystemStartup,
        .ActorType = Audit.ActorType.System,
        .ActorId = SystemActorId,
        .AffectedComponent = "App",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = correlationId,
        .ReasonCode = $"StartupWindowShown:{w.GetType().Name};Mode={decision.Mode};DbPath={decision.DbPath}"
    })

        End Sub

        ' -------------------------
        ' Startup decision model
        ' -------------------------
        Private Enum StartupMode
            Commissioning
            Operational
        End Enum

        Private NotInheritable Class StartupDecision
            Public Property Mode As StartupMode
            Public Property DbPath As String
            Public Property IsCommissioned As Boolean
        End Class

        Private Function DetermineStartupMode(correlationId As String) As StartupDecision
            Dim decision As New StartupDecision With {
        .Mode = StartupMode.Commissioning,
        .DbPath = "",
        .IsCommissioned = False
    }

            Try
                Using db = DatabaseBootstrapper.BuildDbContext()

                    decision.DbPath = db.Database.GetDbConnection().DataSource

                    EnsureKioskStateRow(db)

                    Dim row = db.KioskState.AsNoTracking().
                OrderByDescending(Function(x) x.LastUpdatedUtc).
                FirstOrDefault()

                    decision.IsCommissioned = (row IsNot Nothing AndAlso row.IsCommissioned)
                    decision.Mode = If(decision.IsCommissioned, StartupMode.Operational, StartupMode.Commissioning)

                    SafeAudit(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.SystemStartup,
                .ActorType = Audit.ActorType.System,
                .ActorId = SystemActorId,
                .AffectedComponent = "App",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId,
                .ReasonCode = $"KioskStateEvaluated:IsCommissioned={decision.IsCommissioned};DbPath={decision.DbPath}"
            })

                End Using

            Catch ex As Exception
                SafeAudit(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.SystemStartup,
            .ActorType = Audit.ActorType.System,
            .ActorId = SystemActorId,
            .AffectedComponent = "App",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = correlationId,
            .ReasonCode = $"KioskStateEvalFailed:{ex.GetType().Name};Fallback=Commissioning"
        })

                decision.Mode = StartupMode.Commissioning
                decision.IsCommissioned = False
            End Try

            Return decision
        End Function

        Private Function CreateStartupWindow(decision As StartupDecision) As Window
            If decision Is Nothing Then Throw New ArgumentNullException(NameOf(decision))

            Select Case decision.Mode
                Case StartupMode.Operational
                    Dim lockerController As New LockerControllerService()
                    lockerController.ConnectBranch("A")
                    Try
                        lockerController.ConnectBranch("B")
                    Catch ex As InvalidOperationException
                        ' Branch B not configured yet; ignore for now
                    End Try
                    Return New LockerAccessWindow(lockerController)

                Case Else
                    Return New CommissioningAccessWindow() With {
                .ActorId = BootstrapCommissioningActorId,
                .KioskId = AppSettings.KioskID
            }
            End Select
        End Function

        ' -------------------------
        ' Mutex
        ' -------------------------
        Private Function TryAcquireSingleInstanceMutex() As Boolean
            Dim createdNew As Boolean = False
            _mutex = New Mutex(True, "Global\SmartLockerKiosk_SingleInstance", createdNew)
            Return createdNew
        End Function

        ' -------------------------
        ' DB row creation
        ' -------------------------
        Private Sub EnsureKioskStateRow(db As KioskDbContext)
            Dim row = db.KioskState.
        OrderByDescending(Function(x) x.LastUpdatedUtc).
        FirstOrDefault()

            If row Is Nothing Then
                db.KioskState.Add(New KioskState With {
            .IsCommissioned = False,
            .LastUpdatedUtc = DateTime.UtcNow
        })
                db.SaveChanges()
            End If
        End Sub

        ' -------------------------
        ' Audit helper
        ' -------------------------
        Private Sub SafeAudit(ev As Audit.AuditEvent)
            Try
                Audit.AuditServices.SafeLog(ev)
            Catch
                ' do not crash startup due to auditing
            End Try
        End Sub
        Private Sub ApplyRuntimeConfig(correlationId As String)

            ' =========================
            ' Configure runtime settings
            ' =========================
            AppSettings.BaseApiUrl = "https://smartlockerapp.azurewebsites.net/api/"
            AppSettings.DeviceApiKey = "dev-kiosk-key-123"
            AppSettings.KioskID = "KIOSK-DEV-001"
            AppSettings.LocationId = "LOC-DEV-001"
            'AppSettings.TestModeEnabled = False

            SafeAudit(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
        .ActorType = Audit.ActorType.System,
        .ActorId = SystemActorId,
        .AffectedComponent = "AppSettings",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = correlationId,
        .ReasonCode = "RuntimeConfigApplied"
    })

        End Sub
        Private Sub InitializeLocalDatabaseOrFail(correlationId As String)

            Try
                DatabaseBootstrapper.InitializeDatabase()

                SafeAudit(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.System,
            .ActorId = SystemActorId,
            .AffectedComponent = "DatabaseBootstrapper",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = correlationId,
            .ReasonCode = "DatabaseInitialized"
        })

            Catch ex As Exception

                SafeAudit(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.System,
            .ActorId = SystemActorId,
            .AffectedComponent = "DatabaseBootstrapper",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = correlationId,
            .ReasonCode = $"DatabaseInitFailed:{ex.GetType().Name}"
        })

                ' Fail fast: kiosk cannot operate without a DB
                MessageBox.Show(
            "SmartLockerKiosk could not initialize its local database." & Environment.NewLine &
            "The application will now exit." & Environment.NewLine & Environment.NewLine &
            ex.Message,
            "Startup Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error)

                Current.Shutdown()
            End Try

        End Sub


    End Class
End Namespace
