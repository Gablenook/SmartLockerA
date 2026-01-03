Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Microsoft.EntityFrameworkCore
Imports System.Net.Http.Headers
Imports System.Text.Json

Namespace SmartLockerKiosk
    Partial Public Class LockerAccessWindow
        'Revision 1.00
        Inherits Window
        Private fadeIn As Storyboard
        ' Authorization result from cloud
        Private Enum ScreenState
            AwaitWorkflowChoice
            AwaitCredential
            ValidatingCredential
            AwaitAdminCredential
            AwaitWorkOrder
            AwaitLockerSize
        End Enum
        Public Enum WorkflowMode
            Pickup
            Delivery
            DayUse
        End Enum

        Private _state As ScreenState = ScreenState.AwaitWorkflowChoice
        Private _transactionType As String = ""   ' "DELIVER" or "PICKUP" or "ADMIN"
        Private Shared ReadOnly _http As New HttpClient()
        Private _selectedWorkflow As WorkflowMode? = Nothing
        Private _authResult As AuthResult = Nothing
        Private _lastSubmitUtc As DateTime = DateTime.MinValue
        Private ReadOnly _submitDebounce As TimeSpan = TimeSpan.FromMilliseconds(400)
        Private _lockerController As LockerControllerService
        Private _courierAuth As AuthResult = Nothing
        Private _authorizedWorkOrders As List(Of WorkOrderAuthItem) = New List(Of WorkOrderAuthItem)()
        Private _activeWorkOrder As WorkOrderAuthItem = Nothing
        Private _sizeTiles As List(Of SizeTile) = New List(Of SizeTile)()
        Private _selectedSizeCode As String = Nothing
        Private _sessionToken As String = ""
        Private ReadOnly _assigner As New LockerAssignmentService()
        Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
    .PropertyNameCaseInsensitive = True}
        Private _commitFlushTimer As Threading.DispatcherTimer
        Private _isFlushing As Boolean = False
        Private _lockerSizes As List(Of LockerSize) = New List(Of LockerSize)()






        Public Sub New()
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)
        End Sub
        Private Sub LockerAccessWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            StartPendingCommitFlusher()

            Try
                fadeIn.Begin()
            Catch
                MessageBox.Show("Animation error")
            End Try

            ' Unified input: keypad submits value when ENTER pressed (variable length)
            AddHandler KeypadControl.PasscodeComplete, AddressOf HandleKeypadSubmit

            ' Enforce full-screen manually (Surface-friendly)
            Me.WindowStyle = WindowStyle.None
            Me.ResizeMode = ResizeMode.NoResize
            Me.Topmost = True
            Me.WindowState = WindowState.Normal
            Me.Left = 0
            Me.Top = 0
            Me.Width = SystemParameters.PrimaryScreenWidth
            Me.Height = SystemParameters.PrimaryScreenHeight
            Me.WindowState = WindowState.Maximized

            KeypadControl.AutoSubmitOnLength = False
            KeypadControl.RequireExactLengthOnSubmit = False
            KeypadControl.SetPasscodeLength(20) ' max length cap

            _lockerController = New LockerControllerService()

            ResetToAwaitWorkflowChoice()

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.SystemStartup,
    .ActorType = Audit.ActorType.System,
    .ActorId = "System:SmartLockerKiosk",
    .AffectedComponent = "LockerAccessWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = Guid.NewGuid().ToString("N"),
    .ReasonCode = "LockerAccessWindowLoaded"
})



            FocusHidSink()
        End Sub
        Private Sub FocusHidSink()
            Dispatcher.BeginInvoke(Sub()
                                       HidInputBox.Focus()
                                       Keyboard.Focus(HidInputBox)
                                   End Sub, System.Windows.Threading.DispatcherPriority.Background)
        End Sub
        Private Sub LockerAccessWindow_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            FocusHidSink()
        End Sub
        Private Sub HidInputBox_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles HidInputBox.PreviewKeyDown
            If e.Key <> Key.Enter Then Return

            Dim scanned As String = HidInputBox.Text.Trim()
            HidInputBox.Clear()
            e.Handled = True

            If scanned.Length = 0 Then Return

            RouteScan(scanned, "HID")

        End Sub
        Private Sub RouteScan(raw As String, source As String)
            Dim value = (If(raw, "")).Trim()
            If value.Length = 0 Then Return

            Select Case _state
                Case ScreenState.AwaitWorkOrder
                    SubmitWorkOrder(value, source)

                Case ScreenState.AwaitCredential, ScreenState.AwaitAdminCredential
                    SubmitCredential(value, source)

                Case Else
                    ' Ignore scans in other states (Validating, menu, etc.)
                    Return
            End Select
        End Sub
        Private Async Sub SubmitWorkOrder(rawWorkOrder As String, source As String)

            Dim wo As String = (If(rawWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' --- debounce (same as credential) ---
            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

            ' --- state guards ---
            If _state = ScreenState.ValidatingCredential Then Return
            If Not _selectedWorkflow.HasValue Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            ' --- transition to validating ---
            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            UserPromptText.Text = $"Processing work order {wo}…"
            SafeFadeIn()

            Try
                Select Case _selectedWorkflow.Value

                    Case WorkflowMode.Pickup
                        ' PICKUP: must be authorized from the cloud-auth result list
                        Dim match = FindAuthorizedWorkOrder(wo)

                        If match Is Nothing Then
                            _state = ScreenState.AwaitWorkOrder

                            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.AuthenticationAttempt, ' or WorkOrderSubmitted if you add it
    .ActorType = Audit.ActorType.User,
    .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
    .AffectedComponent = "LockerAccessWindow",
    .Outcome = Audit.AuditOutcome.Denied,
    .CorrelationId = actionId,
    .ReasonCode = $"WorkOrderDenied:NotAuthorized;WO={wo};Source={source}"
})



                            UserPromptText.Text = "Work Order not Recognized"
                            SafeFadeIn()
                            Return
                        End If

                        _activeWorkOrder = match

                        If String.IsNullOrWhiteSpace(match.LockerNumber) Then
                            _state = ScreenState.AwaitWorkOrder
                            UserPromptText.Text = "No locker assigned for W.O."
                            SafeFadeIn()
                            Return
                        End If

                        UserPromptText.Text = $"Opening locker {match.LockerNumber}…"
                        SafeFadeIn()

                        Try
                            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.LockerOpenAttempt,
    .ActorType = Audit.ActorType.User,
    .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
    .AffectedComponent = "LockerControllerService",
    .Outcome = Audit.AuditOutcome.Success, ' assume success; flip on catch
    .CorrelationId = actionId,
    .ReasonCode = $"PickupOpenRequested;WO={wo};Locker={match.LockerNumber}"
})

                            _lockerController.UnlockByLockerNumber(match.LockerNumber)
                            UserPromptText.Text = $"Locker {match.LockerNumber} opened."
                            SafeFadeIn()
                        Catch
                            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.LockerOpenAttempt,
    .ActorType = Audit.ActorType.User,
    .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
    .AffectedComponent = "LockerControllerService",
    .Outcome = Audit.AuditOutcome.Error,
    .CorrelationId = actionId,
    .ReasonCode = $"PickupOpenFailed;WO={wo};Locker={match.LockerNumber}"
})

                            UserPromptText.Text = $"Unable to open locker {match.LockerNumber}. Please contact attendant."
                            SafeFadeIn()
                        End Try

                        Await Task.Delay(1500)
                        ResetToAwaitWorkflowChoice()
                        Return


                    Case WorkflowMode.Delivery
                        ' DELIVERY: paperwork is authority; do NOT validate against authorized list.
                        _activeWorkOrder = New WorkOrderAuthItem With {
                    .WorkOrderNumber = wo,
                    .TransactionType = "Delivery",
                    .LockerNumber = "",
                    .AllowedSizeCode = ""
                }
                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' or WorkOrderCaptured
    .ActorType = Audit.ActorType.User,
    .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
    .AffectedComponent = "LockerAccessWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = actionId,
    .ReasonCode = $"DeliveryWorkOrderCaptured;WO={wo};Source={source}"
})


                        UserPromptText.Text = $"Select compartment size."
                        SafeFadeIn()

                        _state = ScreenState.AwaitLockerSize

                        ' Show size selection UI (this should set panel visibility, populate tiles, etc.)
                        ShowLockerSizeSelection()
                        Return


                    Case Else
                        ResetToAwaitWorkflowChoice()
                        Return

                End Select

            Catch ex As Exception
                ResetToAwaitWorkflowChoice()
                UserPromptText.Text = "System unavailable"
                SafeFadeIn()

            Finally
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
            End Try

        End Sub
        Private Function FindAuthorizedWorkOrder(scannedWorkOrder As String) As WorkOrderAuthItem
            Dim wo = (If(scannedWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return Nothing

            If _authorizedWorkOrders Is Nothing OrElse _authorizedWorkOrders.Count = 0 Then
                Return Nothing
            End If

            Dim desiredTxn As String =
        If(_selectedWorkflow.HasValue AndAlso _selectedWorkflow.Value = WorkflowMode.Delivery,
           "Delivery",
           "Pickup")

            ' Exact match on WO number; and filter by txn if provided
            Return _authorizedWorkOrders.
        FirstOrDefault(Function(x) x IsNot Nothing AndAlso
                                   wo.Equals((If(x.WorkOrderNumber, "")).Trim(), StringComparison.OrdinalIgnoreCase) AndAlso
                                   (String.IsNullOrWhiteSpace(x.TransactionType) OrElse
                                    desiredTxn.Equals(x.TransactionType.Trim(), StringComparison.OrdinalIgnoreCase)))
        End Function
        Private Sub HandleKeypadSubmit(sender As Object, value As String)
            RouteScan(value, "KEYPAD")
        End Sub
        Private Sub ResetToAwaitWorkflowChoice()
            _state = ScreenState.AwaitWorkflowChoice
            _selectedWorkflow = Nothing
            _authResult = Nothing

            UserPromptText.Text = "Select Pickup or Delivery"
            SafeFadeIn()

            PickupButton.IsEnabled = True
            DeliverButton.IsEnabled = True
            'DayUseButton.IsEnabled = True   ' add this button in XAML

            _authorizedWorkOrders.Clear()
            _activeWorkOrder = Nothing
            _courierAuth = Nothing

            SizeSelectionPanel.Visibility = Visibility.Collapsed
            SizeTilesItems.ItemsSource = Nothing

            KeypadControl.Reset()
            FocusHidSink()
        End Sub
        Private Sub PromptForCredential()
            _state = ScreenState.AwaitCredential
            _authResult = Nothing

            PickupButton.IsEnabled = False
            DeliverButton.IsEnabled = False
            'DayUseButton.IsEnabled = False

            UserPromptText.Text = "Enter Credential"
            SafeFadeIn()

            KeypadControl.Reset()
            FocusHidSink()
        End Sub
        Private Async Sub SubmitCredential(rawCredential As String, source As String)

            ' ===== Normalize =====
            Dim credential As String = (If(rawCredential, "")).Trim()
            If credential.Length = 0 Then Return ' don't consume debounce

            ' ===== Debounce + re-entry =====
            Dim nowUtc As DateTime = DateTime.UtcNow
            If (nowUtc - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = nowUtc

            If _state = ScreenState.ValidatingCredential Then Return

            ' ===== Determine flow =====
            Dim isAdminFlow As Boolean = (_state = ScreenState.AwaitAdminCredential)

            ' Only accept credential input in these states
            If _state <> ScreenState.AwaitCredential AndAlso Not isAdminFlow Then Return

            ' For non-admin, must have a workflow selected
            If Not isAdminFlow AndAlso Not _selectedWorkflow.HasValue Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            Dim purpose As AuthPurpose =
        If(isAdminFlow, AuthPurpose.AdminAccess, GetPurposeForWorkflow(_selectedWorkflow.Value))

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' ===== Enter busy state =====
            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            ShowPrompt("Validating…")

            Dim result As AuthResult = Nothing

            Try
                ' IMPORTANT: pass source so credentialType can be correct (Pin vs Badge etc.)
                result = Await ValidateCredentialWithServerAsync(credential, purpose, source)


            Catch
                ' Backend/network failure: audit as Error and reset UI
                AuditAuthError(actionId, isAdminFlow, "AuthServiceUnavailable")
                ResetToAwaitWorkflowChoice()
                ShowPrompt("System unavailable")
                Exit Sub

            Finally
                ' Always restore interactivity
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
            End Try

            ' ===== Evaluate result (no Await needed below) =====
            If result Is Nothing OrElse Not result.IsAuthorized Then
                AuditAuthDenied(actionId, isAdminFlow, If(purpose = AuthPurpose.AdminAccess, "AdminAuthDenied", "UserAuthDenied"))

                _state = If(isAdminFlow, ScreenState.AwaitAdminCredential, ScreenState.AwaitCredential)
                ShowPrompt(If(result?.Message, "Credential not recognized"))
                Return
            End If

            ' Persist token for subsequent backend calls
            _sessionToken = (If(result.SessionToken, "")).Trim()

            ' Persist auth context
            _authResult = result
            _authorizedWorkOrders = If(result.WorkOrders, New List(Of WorkOrderAuthItem)())
            _activeWorkOrder = Nothing

            ' Audit success using validated identity
            AuditAuthSucceeded(actionId, purpose, result)

            ' Admin short-circuit
            If purpose = AuthPurpose.AdminAccess Then
                Await ShowAdminPanelAsync(result)
                ResetToAwaitWorkflowChoice()
                Return
            End If

            ' Continue workflow
            Await ContinueWorkflowAfterAuthAsync(_selectedWorkflow.Value, result)

        End Sub
        Private Async Function ValidateCredentialWithServerAsync(
    scanValue As String,
    purpose As AuthPurpose,
    source As String
) As Task(Of AuthResult)

            Dim credential = (If(scanValue, "")).Trim()
            If credential.Length = 0 Then
                Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = "Empty credential."}
            End If

            ' === TEST MODE SHORT-CIRCUIT ===
            If AppSettings.TestModeEnabled Then
                Dim local = TestAuthProvider.Authorize(credential, purpose)

                If local Is Nothing Then
                    Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = "Test auth provider returned nothing."}
                End If

                If local.IsAuthorized Then
                    _sessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")

                    ' Provide predictable Pickup work orders in Test mode
                    If purpose = AuthPurpose.PickupAccess Then
                        If local.WorkOrders Is Nothing Then local.WorkOrders = New List(Of WorkOrderAuthItem)()

                        local.WorkOrders.Add(New WorkOrderAuthItem With {
                .WorkOrderNumber = AppSettings.TestWorkOrder,
                .TransactionType = "Pickup",
                .LockerNumber = AppSettings.TestPickupLockerNumber,  ' add this setting, e.g. "A-001"
                .AllowedSizeCode = ""
            })
                    End If
                End If

                Return local
            End If

            ' ===============================

            ' --- real backend call continues here ---
            Dim requestId = Guid.NewGuid().ToString("N")

            Dim credentialType As String =
        If(source IsNot Nothing AndAlso source.Equals("KEYPAD", StringComparison.OrdinalIgnoreCase), "Pin", "Badge")

            Dim reqObj = New With {
        .credential = credential,
        .credentialType = credentialType,
        .purpose = purpose.ToString(),
        .kioskId = AppSettings.KioskID,
        .locationId = AppSettings.LocationId,
        .timestampUtc = DateTime.UtcNow.ToString("o"),
        .requestId = requestId
    }

            Dim json = System.Text.Json.JsonSerializer.Serialize(reqObj)

            Using msg = CreateJsonRequest(HttpMethod.Post, "/v1/auth/authorize", requestId, json)
                Dim resp = Await _http.SendAsync(msg)
                Dim body = Await resp.Content.ReadAsStringAsync()

                If Not resp.IsSuccessStatusCode Then
                    Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = $"Auth failed ({CInt(resp.StatusCode)})."}
                End If

                Dim dto = System.Text.Json.JsonSerializer.Deserialize(Of AuthorizeResponseDto)(
            body, New System.Text.Json.JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
        )

                Dim isAuth As Boolean = (dto IsNot Nothing AndAlso dto.isAuthorized)

                Dim result As New AuthResult With {
    .IsAuthorized = isAuth,
    .Purpose = purpose,
    .UserId = dto?.userId,
    .DisplayName = dto?.displayName,
    .Message = If(isAuth, "OK", "Credential not recognized"),
    .NextAction = "PROMPT_WORK_ORDER",
    .WorkOrders = New List(Of WorkOrderAuthItem)()
}


                If dto.workOrders IsNot Nothing Then
                    For Each w In dto.workOrders
                        result.WorkOrders.Add(New WorkOrderAuthItem With {
                    .WorkOrderNumber = w.workOrderNumber,
                    .TransactionType = w.transactionType,
                    .LockerNumber = w.lockerNumber,
                    .AllowedSizeCode = w.allowedSizeCode
                })
                    Next
                End If

                _sessionToken = dto.sessionToken
                Return result
            End Using
        End Function

        Private Function MapPurposeForBackend(p As AuthPurpose) As String
            Select Case p
                Case AuthPurpose.PickupAccess
                    Return "Pickup"
                Case AuthPurpose.DeliveryCourierAuth
                    Return "Deliver"
                Case AuthPurpose.AdminAccess
                    Return "Admin"
                Case AuthPurpose.DayUseStart
                    Return "DayUse"
                Case Else
                    Return "Pickup"
            End Select
        End Function
        Private Function MapCredentialTypeForBackend(source As String, credential As String) As String
            Dim s = (If(source, "")).Trim().ToUpperInvariant()

            ' You can tune this later (Barcode/QR detection etc.)
            If s = "KEYPAD" Then Return "Pin"

            ' Common default for HID badge scanners
            Return "Badge"
        End Function
        Private Function ExtractBackendErrorMessage(body As String, resp As HttpResponseMessage) As String
            ' If backend returns: { "errorCode": "...", "message": "...", "requestId": "..." }
            Try
                Dim err = System.Text.Json.JsonSerializer.Deserialize(Of BackendErrorDto)(
            body,
            New System.Text.Json.JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
        )

                If err IsNot Nothing Then
                    Dim msg = (If(err.message, "")).Trim()
                    If msg.Length > 0 Then
                        Return msg & $" (HTTP {CInt(resp.StatusCode)})"
                    End If
                End If
            Catch
                ' ignore parse errors
            End Try

            Return $"Auth failed (HTTP {CInt(resp.StatusCode)})."
        End Function
        Private Function GetPurposeForWorkflow(mode As WorkflowMode) As AuthPurpose
            Select Case mode
                Case WorkflowMode.Pickup
                    Return AuthPurpose.PickupAccess
                Case WorkflowMode.Delivery
                    Return AuthPurpose.DeliveryCourierAuth
                Case WorkflowMode.DayUse
                    Return AuthPurpose.DayUseStart
                Case Else
                    Throw New InvalidOperationException("Unknown workflow mode.")
            End Select
        End Function
        Private Async Function ContinueWorkflowAfterAuthAsync(mode As WorkflowMode, result As AuthResult) As Task
            Select Case mode
                Case WorkflowMode.Pickup
                    UserPromptText.Text = "Scan Work Order"
                    SafeFadeIn()
                    _state = ScreenState.AwaitWorkOrder
                    Return

                Case WorkflowMode.Delivery
                    _courierAuth = result
                    UserPromptText.Text = "Scan Work Order."
                    SafeFadeIn()
                    _state = ScreenState.AwaitWorkOrder
                    Return

                Case Else
                    Throw New InvalidOperationException("Unknown workflow mode.")
            End Select
        End Function
        Private Sub SetUiEnabled(enabled As Boolean)
            KeypadControl.IsEnabled = enabled

            ' During validation, disable menu choices. After validation, ShowTransactionMenu sets them.
            If Not enabled Then
                PickupButton.IsEnabled = False
                DeliverButton.IsEnabled = False
            End If
        End Sub
        Private Sub SafeFadeIn()
            Try : fadeIn.Begin() : Catch : End Try
        End Sub
        Private Sub PickupButton_Click(sender As Object, e As RoutedEventArgs) Handles PickupButton.Click
            If _state <> ScreenState.AwaitWorkflowChoice Then Return

            _selectedWorkflow = WorkflowMode.Pickup

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' or WorkflowSelected if you add it
        .ActorType = Audit.ActorType.User,
        .ActorId = "User:Unknown",
        .AffectedComponent = "LockerAccessWindow",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = "WorkflowSelected:Pickup"
    })

            PromptForCredential()
        End Sub
        Private Sub DeliverButton_Click(sender As Object, e As RoutedEventArgs) Handles DeliverButton.Click
            If _state <> ScreenState.AwaitWorkflowChoice Then Return
            _selectedWorkflow = WorkflowMode.Delivery

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' or WorkflowSelected if you add it
        .ActorType = Audit.ActorType.User,
        .ActorId = "User:Unknown",
        .AffectedComponent = "LockerAccessWindow",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = "WorkflowSelected:Delivery"
    })
            PromptForCredential()
        End Sub

        'Private Sub DayUseButton_Click(sender As Object, e As RoutedEventArgs) Handles DayUseButton.Click
        ' If _state <> ScreenState.AwaitWorkflowChoice Then Return
        '     _selectedWorkflow = WorkflowMode.DayUse
        '     PromptForCredential()
        ' End Sub
        Private Sub AdminLogo_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs)
            If _state = ScreenState.ValidatingCredential Then Return

            _selectedWorkflow = Nothing
            _authResult = Nothing

            _state = ScreenState.AwaitAdminCredential
            UserPromptText.Text = "Enter Admin Credential"
            SafeFadeIn()

            ' Disable workflow buttons while doing admin login
            PickupButton.IsEnabled = False
            DeliverButton.IsEnabled = False

            KeypadControl.Reset()

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.AuthenticationAttempt,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = "Admin:Unknown",
    .AffectedComponent = "LockerAccessWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = Guid.NewGuid().ToString("N"),
    .ReasonCode = "AdminLoginPrompted"
})



            FocusHidSink()
        End Sub
        Private Async Function ShowAdminPanelAsync(result As AuthResult) As Task

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' We already know this is authorized AdminAccess
            Dim adminActorId As String = $"Admin:{result.UserId}"

            ' AUDIT: admin authentication success
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.AuthenticationAttempt,
        .ActorType = Audit.ActorType.Admin,
        .ActorId = adminActorId,
        .AffectedComponent = "AuthService",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = actionId,
        .ReasonCode = "AdminAuthSucceeded"
    })

            Dim w As New AdminScreen With {
        .Owner = Me,
        .AdminActorId = adminActorId
    }

            w.ShowDialog()

            FocusHidSink()

        End Function


        Private Sub HideLockerSizeSelection()
            SizeSelectionPanel.Visibility = Visibility.Collapsed
        End Sub
        Private Async Sub SelectSizeAndAssignLocker(sizeCode As String)

            Dim actionId As String = Guid.NewGuid().ToString("N")
            Dim code As String = (If(sizeCode, "")).Trim().ToUpperInvariant()
            If code.Length = 0 Then Return

            ' Debounce + re-entry guard
            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now
            If _state = ScreenState.ValidatingCredential Then Return

            ' Guards
            If Not _selectedWorkflow.HasValue OrElse _selectedWorkflow.Value <> WorkflowMode.Delivery Then
                ShowPrompt("Size selection is only used for Delivery.")
                Return
            End If

            If _activeWorkOrder Is Nothing OrElse String.IsNullOrWhiteSpace(_activeWorkOrder.WorkOrderNumber) Then
                ShowPrompt("Scan a Work Order first.")
                _state = ScreenState.AwaitWorkOrder
                Return
            End If

            Dim wo As String = _activeWorkOrder.WorkOrderNumber

            ' Enter busy state
            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            HideLockerSizeSelection()

            Dim returnToSizeSelection As Boolean = False
            Dim delayThenReset As Boolean = False

            Try
                ' 1) Select locker locally (no backend dependency)
                ShowPrompt($"Finding an available {code} compartment…")

                Dim assignedLockerNumber As String = _assigner.SelectNextAvailableLockerNumber(code)

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then
                    AuditNoAvailability(actionId, wo, code)
                    ShowPrompt($"No {code} compartments are available. Select a different size.")
                    returnToSizeSelection = True

                Else
                    AuditAssignSucceeded(actionId, wo, code, assignedLockerNumber)

                    ' 2) Open locally
                    ShowPrompt($"Opening locker {assignedLockerNumber}…")

                    If Not TryOpenLockerWithAudit(actionId, wo, assignedLockerNumber) Then
                        ShowPrompt($"Unable to open locker {assignedLockerNumber}. Please contact attendant.")
                        delayThenReset = True

                    Else
                        ' 3) Best-effort backend commit/log
                        ShowPrompt($"Locker {assignedLockerNumber} opened. Logging delivery…")

                        Dim committedOk As Boolean = Await CommitDeliveryToServerSafeAsync(
                    workOrderNumber:=wo,
                    sizeCode:=code,
                    lockerNumber:=assignedLockerNumber,
                    courierAuth:=_courierAuth,
                    correlationId:=actionId
                )

                        If committedOk Then
                            ShowPrompt($"Delivery started. Locker {assignedLockerNumber} opened.")
                        Else
                            ShowPrompt($"Locker {assignedLockerNumber} opened. Delivery will sync when online.")
                        End If

                        delayThenReset = True
                    End If
                End If

            Catch
                ShowPrompt("System unavailable")
                delayThenReset = True

            Finally
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
            End Try

            ' VB rule: await after Try/Catch/Finally
            If returnToSizeSelection Then
                _state = ScreenState.AwaitLockerSize
                ShowLockerSizeSelection()
                Return
            End If

            If delayThenReset Then
                Await Task.Delay(1500)
                ResetToAwaitWorkflowChoice()
                Return
            End If

        End Sub
        Private Sub ShowPrompt(text As String)
            UserPromptText.Text = text
            SafeFadeIn()
        End Sub
        Private Function TryOpenLockerWithAudit(actionId As String, workOrderNumber As String, lockerNumber As String) As Boolean
            Try
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.LockerOpenAttempt,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerControllerService",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"DeliveryOpenRequested;WO={workOrderNumber};Locker={lockerNumber}"
        })

                _lockerController.UnlockByLockerNumber(lockerNumber)
                Return True

            Catch
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.LockerOpenAttempt,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerControllerService",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = $"DeliveryOpenFailed;WO={workOrderNumber};Locker={lockerNumber}"
        })
                Return False
            End Try
        End Function
        Private Sub AuditNoAvailability(actionId As String, workOrderNumber As String, sizeCode As String)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' consider adding LockerAssignmentAttempt
        .ActorType = Audit.ActorType.User,
        .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
        .AffectedComponent = "SelectDeliveryLockerLocally",
        .Outcome = Audit.AuditOutcome.Denied,
        .CorrelationId = actionId,
        .ReasonCode = $"DeliveryAssignDenied:NoAvailability;WO={workOrderNumber};Size={sizeCode}"
    })
        End Sub
        Private Sub AuditAssignSucceeded(actionId As String, workOrderNumber As String, sizeCode As String, lockerNumber As String)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' consider adding LockerAssignmentAttempt
        .ActorType = Audit.ActorType.User,
        .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
        .AffectedComponent = "SelectDeliveryLockerLocally",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = actionId,
        .ReasonCode = $"DeliveryAssignSucceeded;WO={workOrderNumber};Size={sizeCode};Locker={lockerNumber}"
    })
        End Sub
        Private Async Function CommitDeliveryToServerSafeAsync(
    workOrderNumber As String,
    sizeCode As String,
    lockerNumber As String,
    courierAuth As AuthResult,
    correlationId As String
) As Task(Of Boolean)

            Try
                Await PostDeliveryTransactionToServerAsync(
            workOrderNumber,
            sizeCode,
            lockerNumber,
            courierAuth,
            requestId:=correlationId
        )
                Return True

            Catch
                ' 6.3 will enqueue here (you already have that pattern)
                Return False
            End Try

        End Function
        Private Async Function PostDeliveryTransactionToServerAsync(
    workOrderNumber As String,
    sizeCode As String,
    lockerNumber As String,
    courierAuth As AuthResult,
    Optional requestId As String = Nothing
) As Task

            RequireBackendConfig()

            Dim wo = (If(workOrderNumber, "")).Trim()
            Dim sc = (If(sizeCode, "")).Trim().ToUpperInvariant()
            Dim ln = (If(lockerNumber, "")).Trim()

            If wo.Length = 0 Then Throw New ArgumentException("workOrderNumber is required.", NameOf(workOrderNumber))
            If sc.Length = 0 Then Throw New ArgumentException("sizeCode is required.", NameOf(sizeCode))
            If ln.Length = 0 Then Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))

            Dim rid As String = If(String.IsNullOrWhiteSpace(requestId), Guid.NewGuid().ToString("N"), requestId)

            Dim dto As New DeliveryCommitRequestDto With {
        .requestId = rid,
        .timestampUtc = DateTime.UtcNow.ToString("o"),
        .kioskId = AppSettings.KioskID,
        .locationId = AppSettings.LocationId,
        .workOrderNumber = wo,
        .lockerNumber = ln,
        .sizeCode = sc,
        .courierUserId = If(courierAuth IsNot Nothing, courierAuth.UserId, Nothing)
    }

            Dim jsonBody As String = System.Text.Json.JsonSerializer.Serialize(dto)

            ' Endpoint choice: you said you already asked the backend dev for auth rule.
            ' For now, assume Bearer token is used when available.
            Dim msg As HttpRequestMessage =
        CreateJsonRequest(HttpMethod.Post,
                          "/v1/workorders/commit-assignment",
                          rid,
                          jsonBody,
                          bearerToken:=_sessionToken)

            Dim resp As HttpResponseMessage = Nothing
            Dim body As String = ""

            Try
                resp = Await _http.SendAsync(msg)
                body = Await resp.Content.ReadAsStringAsync()

                If resp.IsSuccessStatusCode Then
                    ' Optional: parse response if provided
                    ' Dim result = JsonSerializer.Deserialize(Of DeliveryCommitResponseDto)(body, _jsonOpts)
                    Return
                End If

                ' Handle common failure modes explicitly for debugging
                If resp.StatusCode = Net.HttpStatusCode.Unauthorized OrElse resp.StatusCode = Net.HttpStatusCode.Forbidden Then
                    Throw New InvalidOperationException($"Commit unauthorized ({CInt(resp.StatusCode)}). Check sessionToken / device auth.")
                End If

                If resp.StatusCode = Net.HttpStatusCode.Conflict Then
                    ' Idempotency or already committed – treat as success for kiosk purposes
                    Return
                End If

                Throw New InvalidOperationException($"Commit failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")

            Finally
                If resp IsNot Nothing Then resp.Dispose()
                msg.Dispose()
            End Try
        End Function
        Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub

        Private Function CurrentActorId(actorType As Audit.ActorType) As String
            If _authResult IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_authResult.UserId) Then
                Select Case actorType
                    Case Audit.ActorType.Admin
                        Return $"Admin:{_authResult.UserId}"
                    Case Else
                        Return $"User:{_authResult.UserId}"
                End Select
            End If
            Return If(actorType = Audit.ActorType.Admin, "Admin:Unknown", "User:Unknown")
        End Function
        Private Sub AuditAuthDenied(actionId As String, isAdminFlow As Boolean, reasonCode As String)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AuthenticationAttempt,
                .ActorType = If(isAdminFlow, Audit.ActorType.Admin, Audit.ActorType.User),
                .ActorId = If(isAdminFlow, "Admin:Unknown", "User:Unknown"),
                .AffectedComponent = "AuthService",
                .Outcome = Audit.AuditOutcome.Denied,
                .CorrelationId = actionId,
                .ReasonCode = reasonCode
            })
        End Sub
        Private Sub AuditAuthError(actionId As String, isAdminFlow As Boolean, reasonCode As String)
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AuthenticationAttempt,
                .ActorType = If(isAdminFlow, Audit.ActorType.Admin, Audit.ActorType.User),
                .ActorId = If(isAdminFlow, "Admin:Unknown", "User:Unknown"),
                .AffectedComponent = "AuthService",
                .Outcome = Audit.AuditOutcome.Error,
                .CorrelationId = actionId,
                .ReasonCode = reasonCode
            })
        End Sub
        Private Sub AuditAuthSucceeded(actionId As String, purpose As AuthPurpose, result As AuthResult)
            Dim actorType As Audit.ActorType =
                If(purpose = AuthPurpose.AdminAccess, Audit.ActorType.Admin, Audit.ActorType.User)

            Dim actorId As String =
                If(purpose = AuthPurpose.AdminAccess, $"Admin:{result.UserId}", $"User:{result.UserId}")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AuthenticationAttempt,
                .ActorType = actorType,
                .ActorId = actorId,
                .AffectedComponent = "AuthService",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = actionId,
                .ReasonCode = If(purpose = AuthPurpose.AdminAccess, "AdminAuthSucceeded", "UserAuthSucceeded")
            })
        End Sub
        Private Sub RequireBackendConfig()
            If String.IsNullOrWhiteSpace(AppSettings.BaseApiUrl) Then Throw New InvalidOperationException("BaseApiUrl is not configured.")
            If String.IsNullOrWhiteSpace(AppSettings.KioskID) Then Throw New InvalidOperationException("KioskID is not configured.")
            If String.IsNullOrWhiteSpace(AppSettings.LocationId) Then Throw New InvalidOperationException("LocationId is not configured.")
            If String.IsNullOrWhiteSpace(AppSettings.DeviceApiKey) Then Throw New InvalidOperationException("DeviceApiKey is not configured.")
        End Sub
        Private Function NewRequestId() As String
            Return Guid.NewGuid().ToString("D")
        End Function
        Private Function UtcNowIso() As String
            Return DateTime.UtcNow.ToString("o")
        End Function
        Private Function CreateJsonRequest(method As HttpMethod, relativePath As String, requestId As String, jsonBody As String, Optional bearerToken As String = Nothing) As HttpRequestMessage
            RequireBackendConfig()

            Dim baseUrl = AppSettings.BaseApiUrl.TrimEnd("/"c)
            Dim url = baseUrl & relativePath

            Dim msg As New HttpRequestMessage(method, url)

            ' Correlation / device identity
            msg.Headers.Add("X-Request-Id", requestId)
            msg.Headers.Add("X-Kiosk-Id", AppSettings.KioskID)

            ' Device trust layer (pilot)
            msg.Headers.Add("X-Api-Key", AppSettings.DeviceApiKey)

            ' Session auth layer (after authorize)
            If Not String.IsNullOrWhiteSpace(bearerToken) Then
                msg.Headers.Authorization = New System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken)
            End If

            msg.Content = New StringContent(jsonBody, Encoding.UTF8, "application/json")
            Return msg
        End Function
        Private Async Function PingBackendAsync() As Task(Of String)
            Dim requestId = NewRequestId()
            RequireBackendConfig()

            Dim url = AppSettings.BaseApiUrl.TrimEnd("/"c) & "/health"
            Using msg As New HttpRequestMessage(HttpMethod.Get, url)
                msg.Headers.Add("X-Request-Id", requestId)
                msg.Headers.Add("X-Kiosk-Id", AppSettings.KioskID)
                msg.Headers.Add("X-Api-Key", AppSettings.DeviceApiKey)

                Using resp = Await _http.SendAsync(msg)
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Return $"Health {(CInt(resp.StatusCode))} RequestId={requestId} Body={body}"
                End Using
            End Using
        End Function
        Private Sub StartPendingCommitFlusher()
            _commitFlushTimer = New Threading.DispatcherTimer()
            _commitFlushTimer.Interval = TimeSpan.FromSeconds(20)
            AddHandler _commitFlushTimer.Tick, Async Sub() Await FlushPendingCommitsAsync()
            _commitFlushTimer.Start()
        End Sub
        Private Async Function FlushPendingCommitsAsync() As Task
            If _isFlushing Then Return
            _isFlushing = True

            Try
                If String.IsNullOrWhiteSpace(AppSettings.BaseApiUrl) OrElse
           String.IsNullOrWhiteSpace(AppSettings.KioskID) OrElse
           String.IsNullOrWhiteSpace(AppSettings.DeviceApiKey) Then
                    Return
                End If

                ' If we don't have a session token, we can’t submit user-scoped commits.
                ' Options:
                '   A) require a service token for kiosk to commit (recommended long-term)
                '   B) only flush when a user session exists (pilot acceptable)
                If String.IsNullOrWhiteSpace(_sessionToken) Then Return

                Dim store As New PendingCommitStore()
                Dim items = store.LoadAll()

                If items.Count = 0 Then Return

                For Each item In items.ToList()
                    If item.NextAttemptUtc > DateTime.UtcNow Then Continue For

                    Try
                        ' Use the same backend commit method you already have
                        Await PostDeliveryTransactionToServerAsync(
                    item.WorkOrderNumber,
                    item.SizeCode,
                    item.LockerNumber,
                    New AuthResult With {.UserId = item.SessionUserId}
                )

                        store.RemoveByCommitId(item.CommitId)

                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "DeliveryCommitFlusher",
                    .Outcome = Audit.AuditOutcome.Success,
                    .CorrelationId = item.RequestId,
                    .ReasonCode = $"DeliveryCommitFlushed;WO={item.WorkOrderNumber};Locker={item.LockerNumber}"
                })

                    Catch ex As Exception
                        item.AttemptCount += 1
                        item.LastError = ex.Message

                        ' Backoff: 15s, 30s, 60s, 2m, 5m, 10m (cap)
                        Dim delay As TimeSpan =
                    If(item.AttemptCount <= 1, TimeSpan.FromSeconds(15),
                    If(item.AttemptCount = 2, TimeSpan.FromSeconds(30),
                    If(item.AttemptCount = 3, TimeSpan.FromMinutes(1),
                    If(item.AttemptCount = 4, TimeSpan.FromMinutes(2),
                    If(item.AttemptCount = 5, TimeSpan.FromMinutes(5),
                       TimeSpan.FromMinutes(10))))))

                        item.NextAttemptUtc = DateTime.UtcNow.Add(delay)
                        store.Update(item)
                    End Try
                Next

            Finally
                _isFlushing = False
            End Try
        End Function
        Private Function Truncate(s As String, maxLen As Integer) As String
            Dim t = If(s, "")
            If t.Length <= maxLen Then Return t
            Return t.Substring(0, maxLen)
        End Function

        '********** Locker Size Select During Delivery
        Private Function LoadLockerSizesFromDb() As List(Of LockerSize)
            Using db = DatabaseBootstrapper.BuildDbContext()

                ' Assumes your DbSet is db.LockerSizes
                ' If it’s named differently, adjust here.
                Return db.LockerSizes.AsNoTracking().
            Where(Function(s) s.IsEnabled).
            OrderBy(Function(s) s.SortOrder).
            ThenBy(Function(s) s.SizeCode).
            ToList()

            End Using
        End Function
        Private Sub EnsureSizeTilesLoaded()
            If _lockerSizes IsNot Nothing AndAlso _lockerSizes.Count > 0 AndAlso
       _sizeTiles IsNot Nothing AndAlso _sizeTiles.Count > 0 Then
                Return
            End If

            _lockerSizes = LoadLockerSizesFromDb()

            _sizeTiles = New List(Of SizeTile)()

            For Each s In _lockerSizes
                _sizeTiles.Add(BuildSizeTile(
            sizeCode:=s.SizeCode,
            displayName:=If(String.IsNullOrWhiteSpace(s.DisplayName), s.SizeCode, s.DisplayName),
            widthIn:=s.WidthIn,
            heightIn:=s.HeightIn,
            depthIn:=s.DepthIn
        ))
            Next
        End Sub
        Private Sub BindSizeTilesToUi()
            ' Important: bind to a collection the ItemsControl can enumerate.
            ' A List is fine; ObservableCollection is better if you plan to update dynamically.
            SizeTilesItems.ItemsSource = _sizeTiles
        End Sub
        Private Async Function TryReserveLockerAsync(workOrderNumber As String, requestedLockerNumber As String) As Task(Of Boolean)

            If String.IsNullOrWhiteSpace(_sessionToken) Then
                Return False ' not authorized / session expired
            End If

            Dim requestId = NewRequestId()

            Dim reqObj = New With {
        .workOrderNumber = workOrderNumber,
        .kioskId = AppSettings.KioskID,
        .locationId = AppSettings.LocationId,
        .requestedLockerNumber = requestedLockerNumber,
        .timestampUtc = DateTime.UtcNow.ToString("o"),
        .requestId = requestId
    }

            Dim json = JsonSerializer.Serialize(reqObj)

            Using msg = CreateJsonRequest(HttpMethod.Post, "/v1/workorders/assign-locker", requestId, json, bearerToken:=_sessionToken)
                Using resp = Await _http.SendAsync(msg)

                    If resp.StatusCode = Net.HttpStatusCode.Conflict Then
                        Return False
                    End If

                    If Not resp.IsSuccessStatusCode Then
                        Return False
                    End If

                    Return True
                End Using
            End Using

        End Function
        Private Function BuildSizeTile(sizeCode As String, displayName As String, widthIn As Decimal, heightIn As Decimal, depthIn As Decimal) As SizeTile
            ' Create a front-face thumbnail using width vs height.
            ' Clamp aspect ratio so it never becomes unusable.
            Dim w As Double = Math.Max(1.0, CDbl(widthIn))
            Dim h As Double = Math.Max(1.0, CDbl(heightIn))

            Dim ratio As Double = w / h
            ratio = Math.Max(0.4, Math.Min(2.5, ratio)) ' clamp

            Dim maxW As Double = 220
            Dim maxH As Double = 140

            Dim thumbW As Double
            Dim thumbH As Double

            If ratio >= 1.0 Then
                thumbW = maxW
                thumbH = maxW / ratio
                If thumbH > maxH Then
                    thumbH = maxH
                    thumbW = maxH * ratio
                End If
            Else
                thumbH = maxH
                thumbW = maxH * ratio
                If thumbW > maxW Then
                    thumbW = maxW
                    thumbH = maxW / ratio
                End If
            End If

            Dim dn = (If(displayName, "")).Trim()
            Dim dimText = $"{widthIn:0.#}W × {heightIn:0.#}H × {depthIn:0.#}D"

            Return New SizeTile With {
        .SizeCode = (If(sizeCode, "")).Trim().ToUpper(),
        .DisplayName = dn,
        .DimText = dimText,
        .ThumbWidth = thumbW,
        .ThumbHeight = thumbH
    }
        End Function
        Private Sub SizeTile_Click(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            If btn Is Nothing Then Return

            Dim code = TryCast(btn.Tag, String)
            If String.IsNullOrWhiteSpace(code) Then Return

            ' Kick off assignment/open
            SelectSizeAndAssignLocker(code)
        End Sub
        Private Async Function GetAvailableLockersForSizeAsync(sizeCode As String) As Task(Of List(Of String))
            ' If backend supports it, call:
            ' GET /v1/lockers/availability?locationId=...&sizeCode=...
            ' Otherwise return a local list (not recommended long-term)
            Return New List(Of String) From {$"A-001", $"A-002", $"A-003", $"A-004"}
        End Function
        Private Sub ShowLockerSizeSelection()
            EnsureSizeTilesLoaded()
            BindSizeTilesToUi()

            _selectedSizeCode = Nothing

            SizeSelectionPanel.Visibility = Visibility.Visible
            _state = ScreenState.AwaitLockerSize

            ShowPrompt("Select compartment size.")
        End Sub
    End Class
End Namespace



