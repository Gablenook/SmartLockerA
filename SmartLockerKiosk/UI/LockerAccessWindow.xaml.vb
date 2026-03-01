Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.Audit


Namespace SmartLockerKiosk
    Partial Public Class LockerAccessWindow
        'Revision 1.00
        Inherits Window
        Private fadeIn As Storyboard

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
    .PropertyNameCaseInsensitive = True
}
        Private _commitFlushTimer As Threading.DispatcherTimer
        Private _isFlushing As Boolean = False
        Private _lockerSizes As List(Of LockerSize) = New List(Of LockerSize)()
        Private _uiEpoch As Integer = 0
        Private _loadedOnce As Boolean = False
        Private ReadOnly _instanceId As String = Guid.NewGuid().ToString("N").Substring(0, 8)


        Shared Sub New()
            _jsonOpts.Converters.Add(New JsonStringEnumConverter())
        End Sub
        Public Sub New()
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)
        End Sub
        Private Sub LockerAccessWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

            DatabaseBootstrapper.InitializeDatabase()

            If _loadedOnce Then Return
            _loadedOnce = True

            StartPendingCommitFlusher()

            RemoveHandler KeypadControl.PasscodeComplete, AddressOf HandleKeypadSubmit
            AddHandler KeypadControl.PasscodeComplete, AddressOf HandleKeypadSubmit

            Try
                fadeIn.Begin()
            Catch
                MessageBox.Show("Animation error")
            End Try

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

            Dim myEpoch As Integer = _uiEpoch

            Dim wo As String = (If(rawWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' --- debounce ---
            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

            ' --- state guards ---
            If _state = ScreenState.ValidatingCredential Then Return
            If Not _selectedWorkflow.HasValue Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            ShowPrompt($"Processing work order {wo}…")

            Try
                Select Case _selectedWorkflow.Value

                    Case WorkflowMode.Pickup
                        Dim match = FindAuthorizedWorkOrder(wo)

                        If match Is Nothing Then
                            _state = ScreenState.AwaitWorkOrder

                            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                        .EventType = Audit.AuditEventType.AuthenticationAttempt,
                        .ActorType = Audit.ActorType.User,
                        .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
                        .AffectedComponent = "LockerAccessWindow",
                        .Outcome = Audit.AuditOutcome.Denied,
                        .CorrelationId = actionId,
                        .ReasonCode = $"WorkOrderDenied:NotAuthorized;WO={wo};Source={source}"
                    })

                            ShowPrompt("Work Order not Recognized")
                            Return
                        End If

                        _activeWorkOrder = match

                        If String.IsNullOrWhiteSpace(match.LockerNumber) Then
                            _state = ScreenState.AwaitWorkOrder
                            ShowPrompt("No locker assigned for W.O.")
                            Return
                        End If

                        ShowPrompt($"Opening locker {match.LockerNumber}…")

                        Try
                            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                        .EventType = Audit.AuditEventType.LockerOpenAttempt,
                        .ActorType = Audit.ActorType.User,
                        .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
                        .AffectedComponent = "LockerControllerService",
                        .Outcome = Audit.AuditOutcome.Success,
                        .CorrelationId = actionId,
                        .ReasonCode = $"PickupOpenRequested;WO={wo};Locker={match.LockerNumber}"
                    })

                            '     _lockerController.UnlockByLockerNumber(match.LockerNumber)
                            ShowPrompt($"Locker {match.LockerNumber} opened.")
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

                            ShowPrompt($"Unable to open locker {match.LockerNumber}. Please contact attendant.")
                        End Try

                        Await Task.Delay(1500)
                        If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                        Return

                    Case WorkflowMode.Delivery
                        _activeWorkOrder = New WorkOrderAuthItem With {
                    .WorkOrderNumber = wo,
                    .TransactionType = "Delivery",
                    .LockerNumber = "",
                    .AllowedSizeCode = ""
                }

                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                    .ActorType = Audit.ActorType.User,
                    .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
                    .AffectedComponent = "LockerAccessWindow",
                    .Outcome = Audit.AuditOutcome.Success,
                    .CorrelationId = actionId,
                    .ReasonCode = $"DeliveryWorkOrderCaptured;WO={wo};Source={source}"
                })

                        If myEpoch <> _uiEpoch Then Return

                        ShowPrompt("Select compartment size.")
                        _state = ScreenState.AwaitLockerSize
                        ShowLockerSizeSelection()
                        Return

                    Case Else
                        If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                        Return

                End Select

            Catch
                If myEpoch <> _uiEpoch Then Return
                ResetToAwaitWorkflowChoice()
                ShowPrompt("System unavailable")

            Finally
                If myEpoch = _uiEpoch Then
                    KeypadControl.Reset()
                    SetUiEnabled(True)
                    FocusHidSink()
                End If
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
            AuditTrace($"RESET TO HOME state={_state} epoch={_uiEpoch} time={DateTime.Now:HH:mm:ss.fff}",
               reasonCode:="Trace:ResetToAwaitWorkflowChoice")
            TraceToFile("RESET_TO_HOME")

            BumpUiEpoch()

            _state = ScreenState.AwaitWorkflowChoice
            _selectedWorkflow = Nothing
            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _authorizedWorkOrders.Clear()
            _sessionToken = ""

            SizeSelectionPanel.Visibility = Visibility.Collapsed
            SizeTilesItems.ItemsSource = Nothing
            _selectedSizeCode = Nothing

            PickupButton.IsEnabled = True
            DeliverButton.IsEnabled = True
            KeypadControl.Reset()

            ShowPrompt("Select Pickup or Delivery")
            FocusHidSink()
        End Sub

        Private Sub PromptForCredential()
            BumpUiEpoch()

            _state = ScreenState.AwaitCredential
            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _authorizedWorkOrders.Clear()

            PickupButton.IsEnabled = False
            DeliverButton.IsEnabled = False

            KeypadControl.Reset()
            ShowPrompt("Enter Credential")
            FocusHidSink()
        End Sub
        Private Async Sub SubmitCredential(rawCredential As String, source As String)

            Dim myEpoch As Integer = _uiEpoch ' snapshot at entry

            ' ===== Normalize =====
            Dim credential As String = (If(rawCredential, "")).Trim()
            If credential.Length = 0 Then Return

            ' ===== Debounce =====
            Dim nowUtc As DateTime = DateTime.UtcNow
            If (nowUtc - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = nowUtc

            ' ===== Re-entry guard =====
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

            ' ===== Busy =====
            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            ShowPrompt("Validating…")

            Dim result As AuthResult = Nothing

            Try
                result = Await ValidateCredentialWithServerAsync(credential, purpose, source)

            Catch ex As Exception
                If myEpoch <> _uiEpoch Then Return

                TraceToFile("AUTH_EXCEPTION: " & ex.GetType().FullName & " :: " & ex.Message)

                AuditAuthError(actionId, isAdminFlow, "AuthException:" & ex.GetType().Name)

                ' Keep them on the credential screen so they can retry
                _state = If(isAdminFlow, ScreenState.AwaitAdminCredential, ScreenState.AwaitCredential)

                ShowPrompt("System unavailable (" & ex.GetType().Name & ")")
                Return


            Finally
                ' Restore UI only if still current
                If myEpoch = _uiEpoch Then
                    KeypadControl.Reset()
                    SetUiEnabled(True)
                    FocusHidSink()
                End If
            End Try

            If myEpoch <> _uiEpoch Then Return

            If result Is Nothing OrElse Not result.IsAuthorized Then
                AuditAuthDenied(actionId, isAdminFlow,
                        If(purpose = AuthPurpose.AdminAccess, "AdminAuthDenied", "UserAuthDenied"))

                _state = If(isAdminFlow, ScreenState.AwaitAdminCredential, ScreenState.AwaitCredential)
                ShowPrompt(If(result?.Message, "Credential not recognized"))
                Return
            End If

            ' Success: persist auth context
            _sessionToken = (If(result.SessionToken, "")).Trim()
            _authResult = result
            _authorizedWorkOrders = If(result.WorkOrders, New List(Of WorkOrderAuthItem)())
            _activeWorkOrder = Nothing

            AuditAuthSucceeded(actionId, purpose, result)

            ' Admin short-circuit
            If purpose = AuthPurpose.AdminAccess Then
                Await ShowAdminPanelAsync(result)

                ' Only reset home if nothing else has superseded us
                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If
                Return
            End If

            Await ContinueWorkflowAfterAuthAsync(_selectedWorkflow.Value, result)

        End Sub

        Private Async Function ValidateCredentialWithServerAsync(
    scanValue As String,
    purpose As AuthPurpose,
    source As String
) As Task(Of AuthResult)

            Dim credential As String = (If(scanValue, "")).Trim()
            If credential.Length = 0 Then
                Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = "Empty credential."}
            End If

            ' ============================
            ' TEST MODE: LOCAL AUTH ONLY
            ' ============================
            TraceToFile($"AUTH_VALIDATE_ENTER: TestModeEnabled={AppSettings.TestModeEnabled} purpose={purpose} source={source}")

            If AppSettings.TestModeEnabled Then

                Dim isAuth As Boolean = False
                Dim msg As String = "Credential not recognized (test mode)."
                Dim result As New AuthResult With {
            .IsAuthorized = False,
            .Purpose = purpose,
            .Message = msg,
            .WorkOrders = New List(Of WorkOrderAuthItem)()
        }

                Select Case purpose

                    Case AuthPurpose.AdminAccess
                        isAuth = credential.Equals((If(AppSettings.TestAdminCredential, "")).Trim(),
                                          StringComparison.OrdinalIgnoreCase)
                        If isAuth Then
                            result.IsAuthorized = True
                            result.UserId = "TEST-ADMIN"
                            result.DisplayName = "Test Admin"
                            result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                            result.Message = "OK (test admin)"
                        End If
                        Return result

                    Case AuthPurpose.DeliveryCourierAuth
                        isAuth = credential.Equals((If(AppSettings.TestCourierCredential, "")).Trim(),
                                          StringComparison.OrdinalIgnoreCase)
                        If isAuth Then
                            result.IsAuthorized = True
                            result.UserId = "TEST-COURIER"
                            result.DisplayName = "Test Courier"
                            result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                            result.Message = "OK (test courier)"
                        End If
                        Return result

                    Case AuthPurpose.PickupAccess
                        isAuth = credential.Equals((If(AppSettings.TestPickupCredential, "")).Trim(),
                                          StringComparison.OrdinalIgnoreCase)
                        If isAuth Then
                            result.IsAuthorized = True
                            result.UserId = "TEST-PICKUP"
                            result.DisplayName = "Test User"
                            result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                            result.Message = "OK (test pickup)"

                            ' Provide predictable Pickup work orders in Test mode
                            result.WorkOrders.Add(New WorkOrderAuthItem With {
                        .WorkOrderNumber = AppSettings.TestWorkOrder,
                        .TransactionType = "Pickup",
                        .LockerNumber = AppSettings.TestPickupLockerNumber,
                        .AllowedSizeCode = ""
                    })
                        End If
                        Return result

                    Case Else
                        ' If you add more purposes later, decide here
                        Return result

                End Select
            End If

            ' ============================
            ' REAL MODE: BACKEND AUTH
            ' ============================
            RequireBackendConfig()

            Dim requestId = Guid.NewGuid().ToString("N")

            Dim credentialType As String =
        If(source IsNot Nothing AndAlso source.Equals("KEYPAD", StringComparison.OrdinalIgnoreCase), "Pin", "Badge")

            Dim reqObj = New With {
        .credential = credential,
        .credentialType = credentialType,
        .purpose = purpose,
        .kioskId = AppSettings.KioskID,
        .locationId = AppSettings.LocationId,
        .timestampUtc = DateTime.UtcNow.ToString("o"),
        .requestId = requestId
    }

            Dim json = JsonSerializer.Serialize(reqObj, _jsonOpts)

            Dim relativePath As String = ApiRoutes.AuthAuthorize

            Using msg As HttpRequestMessage = CreateJsonRequest(HttpMethod.Post, relativePath, requestId, json)
                Using resp As HttpResponseMessage = Await _http.SendAsync(msg)
                    Dim body As String = Await resp.Content.ReadAsStringAsync()

                    If Not resp.IsSuccessStatusCode Then
                        TraceToFile($"AUTH_HTTP_FAIL: status={CInt(resp.StatusCode)} reason={resp.ReasonPhrase} uri={resp.RequestMessage?.RequestUri} body={body}")
                        Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = $"Auth failed ({CInt(resp.StatusCode)})."}
                    End If

                    Dim dto As AuthorizeResponseDto = Nothing
                    dto = System.Text.Json.JsonSerializer.Deserialize(Of AuthorizeResponseDto)(
                body, New System.Text.Json.JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
            )

                    Dim isAuth As Boolean = (dto IsNot Nothing AndAlso dto.isAuthorized)

                    Dim result As New AuthResult With {
                .IsAuthorized = isAuth,
                .Purpose = purpose,
                .UserId = If(dto IsNot Nothing, dto.userId, Nothing),
                .DisplayName = If(dto IsNot Nothing, dto.displayName, Nothing),
                .Message = If(isAuth, "OK", "Credential not recognized"),
                .WorkOrders = New List(Of WorkOrderAuthItem)(),
                .SessionToken = If(dto IsNot Nothing, dto.sessionToken, Nothing)
            }

                    If dto IsNot Nothing AndAlso dto.workOrders IsNot Nothing Then
                        For Each w In dto.workOrders
                            result.WorkOrders.Add(New WorkOrderAuthItem With {
                        .WorkOrderNumber = w.workOrderNumber,
                        .TransactionType = w.transactionType,
                        .LockerNumber = w.lockerNumber,
                        .AllowedSizeCode = w.allowedSizeCode
                    })
                        Next
                    End If

                    Return result
                End Using
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

            BumpUiEpoch()

            _selectedWorkflow = WorkflowMode.Pickup
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
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

            BumpUiEpoch()

            _selectedWorkflow = WorkflowMode.Delivery
            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
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

            e.Handled = True  ' IMPORTANT: stop bubbling to parent handlers

            TraceToFile("ADMIN_LOGO_CLICK")

            AuditTrace($"ADMIN LOGO CLICK state={_state} epoch={_uiEpoch} time={DateTime.Now:HH:mm:ss.fff}",
               reasonCode:="Trace:AdminLogoClick")

            ' If we're busy, ignore
            If _state = ScreenState.ValidatingCredential Then Return

            ' If we're already prompting for admin, don't re-enter
            If _state = ScreenState.AwaitAdminCredential Then Return

            BumpUiEpoch()

            ' Enter admin credential mode
            _selectedWorkflow = Nothing
            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _authorizedWorkOrders.Clear()

            _state = ScreenState.AwaitAdminCredential

            PickupButton.IsEnabled = False
            DeliverButton.IsEnabled = False

            KeypadControl.Reset()
            ShowPrompt("Enter Admin Credential")
            FocusHidSink()

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.AuthenticationAttempt,
        .ActorType = Audit.ActorType.Admin,
        .ActorId = "Admin:Unknown",
        .AffectedComponent = "LockerAccessWindow",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = "AdminLoginPrompted"
    })
        End Sub
        Private Function ShowAdminPanelAsync(result As AuthResult) As Task
            Dim actionId As String = Guid.NewGuid().ToString("N")
            Dim ActorId As String = $"Admin:{result.UserId}"

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.AuthenticationAttempt,
        .ActorType = Audit.ActorType.Admin,
        .ActorId = ActorId,
        .AffectedComponent = "AuthService",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = actionId,
        .ReasonCode = "AdminAuthSucceeded"
    })

            Dim w As New AdminScreen With {
        .Owner = Me,
        .ActorId = ActorId
    }

            w.ShowDialog()
            FocusHidSink()

            Return Task.CompletedTask
        End Function
        Private Sub HideLockerSizeSelection()
            SizeSelectionPanel.Visibility = Visibility.Collapsed
        End Sub
        Private Async Sub SelectSizeAndAssignLocker(sizeCode As String)

            Dim myEpoch As Integer = _uiEpoch

            Dim actionId As String = Guid.NewGuid().ToString("N")
            Dim code As String = (If(sizeCode, "")).Trim().ToUpperInvariant()
            If code.Length = 0 Then Return

            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

            If _state = ScreenState.ValidatingCredential Then Return

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

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            HideLockerSizeSelection()

            Dim returnToSizeSelection As Boolean = False
            Dim delayThenReset As Boolean = False

            Try
                ShowPrompt($"Finding an available {code} compartment…")
                Dim assignedLockerNumber As String = _assigner.SelectNextAvailableLockerNumber(code)

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then
                    AuditNoAvailability(actionId, wo, code)
                    ShowPrompt($"No {code} compartments are available. Select a different size.")
                    returnToSizeSelection = True
                Else
                    AuditAssignSucceeded(actionId, wo, code, assignedLockerNumber)

                    ShowPrompt($"Opening locker {assignedLockerNumber}…")

                    If Not TryOpenLockerWithAudit(actionId, wo, assignedLockerNumber) Then
                        ShowPrompt($"Unable to open locker {assignedLockerNumber}. Please contact attendant.")
                        delayThenReset = True
                    Else
                        ShowPrompt($"Locker {assignedLockerNumber} opened. Logging delivery…")

                        Dim committedOk As Boolean = Await CommitDeliveryToServerSafeAsync(
                    workOrderNumber:=wo,
                    sizeCode:=code,
                    lockerNumber:=assignedLockerNumber,
                    courierAuth:=_courierAuth,
                    correlationId:=actionId
                )

                        If myEpoch <> _uiEpoch Then Return

                        If committedOk Then
                            ShowPrompt($"Delivery started. Locker {assignedLockerNumber} opened.")
                        Else
                            ShowPrompt($"Locker {assignedLockerNumber} opened. Delivery will sync when online.")
                        End If

                        delayThenReset = True
                    End If
                End If

            Catch
                If myEpoch <> _uiEpoch Then Return
                ShowPrompt("System unavailable")
                delayThenReset = True

            Finally
                If myEpoch = _uiEpoch Then
                    KeypadControl.Reset()
                    SetUiEnabled(True)
                    FocusHidSink()
                End If
            End Try

            If myEpoch <> _uiEpoch Then Return

            If returnToSizeSelection Then
                _state = ScreenState.AwaitLockerSize
                ShowLockerSizeSelection()
                Return
            End If

            If delayThenReset Then
                Await Task.Delay(1500)
                If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                Return
            End If

        End Sub
        Private Sub ShowPrompt(text As String)
            If text = "Select Pickup or Delivery" OrElse text = "Enter Admin Credential" Then
                TraceToFile("SHOWPROMPT: " & text)
            End If

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

                '_lockerController.UnlockByLockerNumber(lockerNumber)
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

            ' ==========
            ' Normalize
            ' ==========
            Dim wo As String = (If(workOrderNumber, "")).Trim()
            Dim sc As String = (If(sizeCode, "")).Trim().ToUpperInvariant()
            Dim ln As String = (If(lockerNumber, "")).Trim()
            Dim cid As String = (If(correlationId, "")).Trim()

            If String.IsNullOrWhiteSpace(cid) Then
                cid = Guid.NewGuid().ToString("N")
            End If

            ' ==========================
            ' Validate required inputs
            ' ==========================
            If wo.Length = 0 Then Throw New ArgumentException("workOrderNumber is required.", NameOf(workOrderNumber))
            If sc.Length = 0 Then Throw New ArgumentException("sizeCode is required.", NameOf(sizeCode))
            If ln.Length = 0 Then Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))

            ' =========================================
            ' TEST MODE: do NOT call backend at all
            ' =========================================
            If AppSettings.TestModeEnabled Then
                Try
                    TraceToFile($"TESTMODE_COMMIT_SKIP: wo={wo} size={sc} locker={ln} corr={cid}")

                    ' Optional: if you have a local "pending commit" queue and you want to exercise it
                    ' even in test mode, you can enqueue here. Most people prefer to treat test mode
                    ' as "success" and skip persistence.
                    '
                    ' Example (only if you WANT it):
                    ' Dim store As New PendingCommitStore()
                    ' store.Add(New PendingCommitItem With {
                    '     .CommitId = Guid.NewGuid().ToString("N"),
                    '     .RequestId = cid,
                    '     .WorkOrderNumber = wo,
                    '     .SizeCode = sc,
                    '     .LockerNumber = ln,
                    '     .SessionUserId = If(courierAuth?.UserId, ""),
                    '     .AttemptCount = 0,
                    '     .NextAttemptUtc = DateTime.UtcNow
                    ' })

                    Return True
                Catch ex As Exception
                    ' Even in test mode, never break the kiosk flow
                    TraceToFile($"TESTMODE_COMMIT_SKIP_FAIL: corr={cid} ex={ex.GetType().Name}:{ex.Message}")
                    Return True
                End Try
            End If

            ' =========================================
            ' REAL MODE: attempt backend commit (safe)
            ' =========================================
            Try
                Await PostDeliveryTransactionToServerAsync(
            workOrderNumber:=wo,
            sizeCode:=sc,
            lockerNumber:=ln,
            courierAuth:=courierAuth,
            requestId:=cid
        )

                Return True

            Catch ex As Exception
                ' Keep kiosk moving; log + allow caller to show "will sync when online"
                TraceToFile($"COMMIT_SAFE_FAIL: corr={cid} wo={wo} size={sc} locker={ln} ex={ex.GetType().Name}:{ex.Message}")

                ' If you already have a queue/enqueue pattern, call it here.
                ' This is the right place to do it because it's ONLY for real-mode failures.
                '
                ' Example (only if you have these types in your project):
                ' Try
                '     Dim store As New PendingCommitStore()
                '     store.Add(New PendingCommitItem With {
                '         .CommitId = Guid.NewGuid().ToString("N"),
                '         .RequestId = cid,
                '         .WorkOrderNumber = wo,
                '         .SizeCode = sc,
                '         .LockerNumber = ln,
                '         .SessionUserId = If(courierAuth?.UserId, ""),
                '         .AttemptCount = 0,
                '         .NextAttemptUtc = DateTime.UtcNow.AddSeconds(15),
                '         .LastError = ex.Message
                '     })
                ' Catch qex As Exception
                '     TraceToFile($"COMMIT_ENQUEUE_FAIL: corr={cid} ex={qex.GetType().Name}:{qex.Message}")
                ' End Try

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
        .courierUserID = If(courierAuth IsNot Nothing, courierAuth.UserId, Nothing)
    }

            Dim jsonBody As String = System.Text.Json.JsonSerializer.Serialize(dto)

            ' IMPORTANT: no leading "/" so BaseAddress prefix (e.g. /api/) is preserved
            Dim relativePath As String = "v1/workorders/commit-assignment"

            Using msg As HttpRequestMessage =
        CreateJsonRequest(HttpMethod.Post,
                          relativePath,
                          rid,
                          jsonBody,
                          bearerToken:=_sessionToken)

                Using resp As HttpResponseMessage = Await _http.SendAsync(msg)
                    Dim body As String = Await resp.Content.ReadAsStringAsync()

                    If resp.IsSuccessStatusCode Then
                        Return
                    End If

                    ' Log full context for debugging
                    TraceToFile($"COMMIT_HTTP_FAIL: status={CInt(resp.StatusCode)} reason={resp.ReasonPhrase} uri={resp.RequestMessage?.RequestUri} body={body}")

                    ' Handle common failure modes explicitly
                    If resp.StatusCode = Net.HttpStatusCode.Unauthorized OrElse resp.StatusCode = Net.HttpStatusCode.Forbidden Then
                        Throw New InvalidOperationException(
                    $"Commit unauthorized ({CInt(resp.StatusCode)}). Check sessionToken / device auth. Body={Truncate(body, 300)}")
                    End If

                    If resp.StatusCode = Net.HttpStatusCode.Conflict Then
                        ' Idempotency or already committed – treat as success for kiosk purposes
                        Return
                    End If

                    If resp.StatusCode = Net.HttpStatusCode.NotFound Then
                        Throw New InvalidOperationException(
                    $"Commit endpoint not found (404). Check BaseApiUrl (/api?) and route '{relativePath}'. Body={Truncate(body, 300)}")
                    End If

                    Throw New InvalidOperationException(
                $"Commit failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")
                End Using
            End Using

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
            ' Respect TestModeEnabled
            AppSettings.RequireBackendConfig()
            If AppSettings.TestModeEnabled Then Return

            ' Window-level extras (only when not test mode)
            If String.IsNullOrWhiteSpace(AppSettings.LocationId) Then
                Throw New InvalidOperationException("LocationId is not configured.")
            End If
        End Sub

        Private Function NewRequestId() As String
            Return Guid.NewGuid().ToString("D")
        End Function
        Private Function UtcNowIso() As String
            Return DateTime.UtcNow.ToString("o")
        End Function
        Private Function CreateJsonRequest(
    method As HttpMethod,
    relativePath As String,
    requestId As String,
    jsonBody As String,
    Optional bearerToken As String = Nothing
) As HttpRequestMessage

            RequireBackendConfig()

            ' Ensure base address is set once
            If _http.BaseAddress Is Nothing Then
                Dim baseUrl = (If(AppSettings.BaseApiUrl, "")).Trim()
                If Not baseUrl.EndsWith("/") Then baseUrl &= "/"
                _http.BaseAddress = New Uri(baseUrl, UriKind.Absolute)
            End If

            ' Force relative path semantics (no leading slash)
            Dim path = (If(relativePath, "")).Trim().TrimStart("/"c)

            Dim msg As New HttpRequestMessage(method, New Uri(path, UriKind.Relative))

            ' Correlation / device identity
            msg.Headers.Add("X-Request-Id", requestId)
            msg.Headers.Add("X-Kiosk-Id", AppSettings.KioskID)
            msg.Headers.Add("X-Api-Key", AppSettings.DeviceApiKey)

            If Not String.IsNullOrWhiteSpace(bearerToken) Then
                msg.Headers.Authorization =
            New System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken)
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
        Private Sub BumpUiEpoch()
            System.Threading.Interlocked.Increment(_uiEpoch)
        End Sub
        Private Sub AuditTrace(message As String,
                       Optional reasonCode As String = Nothing,
                       Optional correlationId As String = Nothing,
                       Optional ex As Exception = Nothing)

            Try
                Dim details As String =
                    message &
                    If(ex IsNot Nothing, Environment.NewLine & ex.ToString(), "") &
                    Environment.NewLine & Environment.StackTrace

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.PolicyConfigurationChange, ' generic "trace" bucket
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "LockerAccessWindow",
                    .Outcome = Audit.AuditOutcome.Success,
                    .CorrelationId = If(String.IsNullOrWhiteSpace(correlationId), Guid.NewGuid().ToString("N"), correlationId),
                    .ReasonCode = If(String.IsNullOrWhiteSpace(reasonCode), details, reasonCode & ";" & details)
                })
            Catch
                ' never let logging break the kiosk
            End Try
        End Sub
        Private Sub TraceToFile(tag As String)
            Try
                Dim path = "C:\ProgramData\SmartLockerKiosk\Logs\ui-trace.txt"
                Dim line =
                    $"{DateTime.Now:HH:mm:ss.fff} [{tag}] inst={_instanceId} state={_state} epoch={_uiEpoch}{Environment.NewLine}" &
                    Environment.StackTrace & Environment.NewLine & Environment.NewLine
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path))
                System.IO.File.AppendAllText(path, line, System.Text.Encoding.UTF8)
            Catch
            End Try
        End Sub
    End Class
End Namespace



