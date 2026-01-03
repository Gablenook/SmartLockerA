Imports System.Windows
Imports System.Windows.Controls


Namespace SmartLockerKiosk

    Partial Public Class AdminScreen
        Inherits Window

        ' Set this when you open AdminScreen (e.g., "Admin:Kevin" or "Admin:Badge123")
        Public Property AdminActorId As String = "Admin:Unknown"

        ' One correlation id for this admin session (open -> close)
        Private ReadOnly _sessionCorrelationId As String = Guid.NewGuid().ToString("N")

        Public Sub New()
            InitializeComponent()
            LoadSettingsIntoUi()

            ' Admin screen opened (session started)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AdminLogin,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = AdminActorId,
                .AffectedComponent = "AdminScreen",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = _sessionCorrelationId,
                .ReasonCode = "AdminScreenOpened"
            })
        End Sub
        Private Sub LoadSettingsIntoUi()
            ' Passcode length
            If AppSettings.PasscodeLength > 0 Then
                SelectComboBoxItemByContent(PasscodeLengthComboBox, AppSettings.PasscodeLength.ToString())
            End If

            ' Workflow
            If Not String.IsNullOrWhiteSpace(AppSettings.SelectedWorkFlow) Then
                SelectComboBoxItemByContent(WorkflowComboBox, AppSettings.SelectedWorkFlow)
            End If

            ' Style
            If Not String.IsNullOrWhiteSpace(AppSettings.SelectedStyle) Then
                SelectComboBoxItemByContent(StyleComboBox, AppSettings.SelectedStyle)
            End If
        End Sub
        Private Sub SelectComboBoxItemByContent(cb As ComboBox, content As String)
            For Each obj In cb.Items
                Dim item = TryCast(obj, ComboBoxItem)
                If item IsNot Nothing AndAlso String.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase) Then
                    cb.SelectedItem = item
                    Exit For
                End If
            Next
        End Sub
        Private Sub LockerSetup_Click(sender As Object, e As RoutedEventArgs)
            Dim actionId = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = AdminActorId,
                .AffectedComponent = "AdminScreen",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = actionId,
                .ReasonCode = "OpenLockerSetupWindow"
            })

            Dim w As New LockerSetupWindow With {.Owner = Me, .AdminActorID = Me.AdminActorId}
            w.ShowDialog()
        End Sub
        Private Sub LockerStatus_Click(sender As Object, e As RoutedEventArgs)
            Dim w As New LockerStatusAdmin With {.Owner = Me}
            w.ShowDialog()
        End Sub
        Private Sub LockerSizes_Click(sender As Object, e As RoutedEventArgs)
            Dim w As New LockerSizeSetupWindow() With {
        .Owner = Me
    }
            w.ShowDialog()
        End Sub
        Private Sub ControllerSetup_Click(sender As Object, e As RoutedEventArgs)
            Dim actionId = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = AdminActorId,
                .AffectedComponent = "AdminScreen",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = actionId,
                .ReasonCode = "OpenControllerSetupWindow"
            })

            Dim w As New ControllerSetupWindow With {
     .Owner = Me,
     .AdminActorId = Me.AdminActorId
 }
            w.ShowDialog()

        End Sub
        Private Sub ExitApp_Click(sender As Object, e As RoutedEventArgs)
            Dim actionId = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.SystemShutdown,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = AdminActorId,
                .AffectedComponent = "AdminScreen",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = actionId,
                .ReasonCode = "AdminRequestedShutdown"
            })

            Application.Current.Shutdown()
        End Sub
        Private Sub ExitButton_Click(sender As Object, e As RoutedEventArgs)
            ' Save selected values to AppSettings
            Dim actionId = Guid.NewGuid().ToString("N")

            Try
                Dim selectedPasscodeLengthItem = TryCast(PasscodeLengthComboBox.SelectedItem, ComboBoxItem)
                If selectedPasscodeLengthItem IsNot Nothing Then
                    Dim n As Integer
                    If Integer.TryParse(selectedPasscodeLengthItem.Content?.ToString(), n) Then
                        AppSettings.PasscodeLength = n
                    End If
                End If

                Dim selectedWorkflowItem = TryCast(WorkflowComboBox.SelectedItem, ComboBoxItem)
                If selectedWorkflowItem IsNot Nothing Then
                    AppSettings.SelectedWorkFlow = selectedWorkflowItem.Content?.ToString()
                End If

                Dim selectedStyleItem = TryCast(StyleComboBox.SelectedItem, ComboBoxItem)
                If selectedStyleItem IsNot Nothing Then
                    AppSettings.SelectedStyle = selectedStyleItem.Content?.ToString()
                End If

                ' Audit: settings committed (don’t log the values unless you decide they are non-sensitive)
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                    .ActorType = Audit.ActorType.Admin,
                    .ActorId = AdminActorId,
                    .AffectedComponent = "AdminScreen",
                    .Outcome = Audit.AuditOutcome.Success,
                    .CorrelationId = actionId,
                    .ReasonCode = "AdminSettingsSaved"
                })

                Me.Close()

            Catch
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                    .ActorType = Audit.ActorType.Admin,
                    .ActorId = AdminActorId,
                    .AffectedComponent = "AdminScreen",
                    .Outcome = Audit.AuditOutcome.Error,
                    .CorrelationId = actionId,
                    .ReasonCode = "AdminSettingsSaveFailed"
                })
                Throw
            End Try
        End Sub
        Protected Overrides Sub OnClosed(e As EventArgs)
            MyBase.OnClosed(e)

            ' Admin session ended
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AdminLogout,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = AdminActorId,
                .AffectedComponent = "AdminScreen",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = _sessionCorrelationId,
                .ReasonCode = "AdminScreenClosed"
            })
        End Sub

    End Class
End Namespace

