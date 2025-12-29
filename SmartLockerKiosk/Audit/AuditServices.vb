Imports System.IO

Namespace Audit

    Public Module AuditServices

        ' Single logger instance for the whole app
        Public Property Logger As IAuditLogger

        Public Sub Initialize()
            ' Store logs under ProgramData so kiosk users can't easily tamper
            Dim baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SmartLockerKiosk",
                "Logs"
            )

            Dim logPath = Path.Combine(baseDir, "audit.log.jsonl")
            Logger = New FileAuditLogger(logPath)

            ' Optional: log logger initialization as a system event
            SafeLog(New AuditEvent With {
                .EventType = AuditEventType.SystemStartup,
                .ActorType = ActorType.System,
                .ActorId = "System:SmartLockerKiosk",
                .AffectedComponent = "AuditLogger",
                .Outcome = AuditOutcome.Success,
                .CorrelationId = Guid.NewGuid().ToString("N"),
                .ReasonCode = "LoggerInitialized"
            })
        End Sub

        Public Sub SafeLog(ev As AuditEvent)
            Try
                If Logger IsNot Nothing Then
                    Logger.Log(ev)
                End If
            Catch
                ' Intentionally swallow: audit logging must not crash kiosk UI.
                ' Later you can route this to Windows Event Log / fallback sink.
            End Try
        End Sub

    End Module

End Namespace

