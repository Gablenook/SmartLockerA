Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
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
        Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True
}
        Private _commitFlushTimer As Threading.DispatcherTimer
        Private _isFlushing As Boolean = False
        Private _lockerSizes As List(Of LockerSize) = New List(Of LockerSize)()
        Private _uiEpoch As Integer = 0
        Private _loadedOnce As Boolean = False
        Private ReadOnly _instanceId As String = Guid.NewGuid().ToString("N").Substring(0, 8)
        Private ReadOnly _backend As IOperationsBackendService
        Private _deliverAnotherTcs As TaskCompletionSource(Of Boolean?) = Nothing
        Private _deliverAnotherTimeoutCts As System.Threading.CancellationTokenSource = Nothing

        Private ReadOnly _scanner As New BarcodeScanService()


        Shared Sub New()
            _jsonOpts.Converters.Add(New JsonStringEnumConverter())
        End Sub
        Public Sub New(lockerController As LockerControllerService)
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)

            _lockerController = lockerController

            If AppSettings.UseBackendBypass Then
                _backend = New BypassOperationsBackendService()
            Else
                _backend = New OperationsBackendService(BackendHttpFactory.CreateHttpClient())
            End If
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
            KeypadControl.SetPasscodeLength(20)

            ' Scanner wiring
            RemoveHandler _scanner.ScanCompleted, AddressOf OnScanCompleted
            RemoveHandler _scanner.ScanRejected, AddressOf OnScanRejected
            RemoveHandler _scanner.Trace, AddressOf OnScannerTrace

            AddHandler _scanner.ScanCompleted, AddressOf OnScanCompleted
            AddHandler _scanner.ScanRejected, AddressOf OnScanRejected
            AddHandler _scanner.Trace, AddressOf OnScannerTrace

            _scanner.IsEnabled = True
            _scanner.Reset()

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
            Dispatcher.BeginInvoke(
        Sub()
            If HidInputBox Is Nothing Then Return
            HidInputBox.Focus()
            Keyboard.Focus(HidInputBox)
        End Sub,
        System.Windows.Threading.DispatcherPriority.Background)
        End Sub
        Private Sub LockerAccessWindow_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            FocusHidSink()
        End Sub
        Private Sub HidInputBox_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles HidInputBox.PreviewKeyDown
            If e Is Nothing Then Return

            If e.Key = Key.Enter OrElse e.Key = Key.Return Then
                HidInputBox.Clear()
                e.Handled = True
            End If
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
        Private Sub Window_PreviewTextInput(sender As Object, e As TextCompositionEventArgs) Handles Me.PreviewTextInput
            If e Is Nothing Then Return
            If String.IsNullOrEmpty(e.Text) Then Return

            _scanner.HandleTextInput(e.Text)
        End Sub
        Private Sub Window_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles Me.PreviewKeyDown
            If e Is Nothing Then Return

            _scanner.HandleKeyDown(e.Key)

            If e.Key = Key.Enter OrElse e.Key = Key.Return Then
                e.Handled = True
            End If
        End Sub
        Private Sub OnScanCompleted(scanText As String)
            Dim value As String = If(scanText, String.Empty).Trim()
            If value.Length = 0 Then Return

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
        .ActorType = Audit.ActorType.System,
        .ActorId = "System:Scanner",
        .AffectedComponent = "BarcodeScanService",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = $"ScanCompleted;State={_state};Value={value}"
    })

            RouteScan(value, "HID")
        End Sub
        Private Sub OnScanRejected(reason As String, rawText As String)
            Dim safeReason As String = If(reason, "Scanner input rejected")
            Dim safeRaw As String = If(rawText, String.Empty)

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
        .ActorType = Audit.ActorType.System,
        .ActorId = "System:Scanner",
        .AffectedComponent = "BarcodeScanService",
        .Outcome = Audit.AuditOutcome.Failure,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = $"ScanRejected;State={_state};Reason={safeReason};Raw={safeRaw}"
    })

            Select Case _state
                Case ScreenState.AwaitCredential,
             ScreenState.AwaitAdminCredential,
             ScreenState.AwaitWorkOrder

                    ShowPrompt("Scan not recognized. Please try again.")
                    FocusHidSink()
            End Select
        End Sub
        Private Sub OnScannerTrace(message As String)
            Dim safeMessage As String = If(message, String.Empty)

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.PolicyConfigurationChange,
        .ActorType = Audit.ActorType.System,
        .ActorId = "System:Scanner",
        .AffectedComponent = "BarcodeScanService",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = Guid.NewGuid().ToString("N"),
        .ReasonCode = $"ScannerTrace;State={_state};Message={safeMessage}"
    })
        End Sub

        'Pickup workflow
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

            Dim matches = _authorizedWorkOrders.
        Where(Function(x) x IsNot Nothing AndAlso
                          wo.Equals((If(x.WorkOrderNumber, "")).Trim(), StringComparison.OrdinalIgnoreCase) AndAlso
                          (String.IsNullOrWhiteSpace(x.TransactionType) OrElse
                           desiredTxn.Equals(x.TransactionType.Trim(), StringComparison.OrdinalIgnoreCase))).
        ToList()

            If matches.Count = 0 Then
                Return Nothing
            End If

            If matches.Count > 1 Then
                Throw New InvalidOperationException(
            $"Multiple authorized records were returned for work order {wo} and transaction type {desiredTxn}.")
            End If

            Return matches(0)
        End Function
        Private Function GetReservedLockerNumbersForWorkOrder(workOrderNumber As String) As List(Of String)
            Dim wo = (If(workOrderNumber, "")).Trim()
            If wo.Length = 0 Then Return New List(Of String)()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim lockerNumbers = db.Lockers.
            AsNoTracking().
            Include(Function(l) l.Status).
            Where(Function(l) l.IsEnabled).
            Where(Function(l) l.Status IsNot Nothing).
            Where(Function(l) (l.Status.OccupancyState = OccupancyState.Reserved) OrElse
                              (l.Status.OccupancyState = OccupancyState.Occupied)).
            Where(Function(l) l.Status.ReservedWorkOrderNumber IsNot Nothing AndAlso
                              l.Status.ReservedWorkOrderNumber.Trim().ToUpper() = wo.ToUpper()).
            OrderBy(Function(l) l.RelayId).
            ThenBy(Function(l) l.LockerNumber).
            Select(Function(l) l.LockerNumber).
            ToList()

                Return lockerNumbers.
            Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
            Select(Function(x) x.Trim()).
            ToList()

            End Using
        End Function
        Private Function TryOpenLockerWithAudit(actionId As String,
                                        workOrderNumber As String,
                                        lockerNumber As String) As Boolean
            Try
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.LockerOpenAttempt,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerControllerService",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"PickupOpenRequested;WO={workOrderNumber};Locker={lockerNumber}"
        })

                Dim opened = _lockerController.UnlockByLockerNumber(lockerNumber)
                If Not opened Then
                    Throw New InvalidOperationException($"Unlock command was not accepted for locker {lockerNumber}.")
                End If

                Return True

            Catch ex As Exception
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.LockerOpenAttempt,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_authResult IsNot Nothing, $"User:{_authResult.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerControllerService",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = $"PickupOpenFailed;WO={workOrderNumber};Locker={lockerNumber};Error={ex.GetType().Name}"
        })

                ShowPrompt($"Unable to open locker {lockerNumber}. Please contact attendant.")
                Return False
            End Try
        End Function
        Private Async Function ProcessPickupWorkOrderAsync(wo As String,
                                                   source As String,
                                                   actionId As String,
                                                   myEpoch As Integer) As Task
            Dim authMatch = FindAuthorizedWorkOrder(wo)

            If authMatch Is Nothing Then
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

            _activeWorkOrder = authMatch

            Dim reservedLockers = GetReservedLockerNumbersForWorkOrder(wo)

            If reservedLockers.Count = 0 Then
                _state = ScreenState.AwaitWorkOrder
                ShowPrompt("No reserved locker was found for this work order.")
                Return
            End If

            ShowPrompt($"Opening locker(s) {String.Join(", ", reservedLockers)}…")

            Dim openedLockers As New List(Of String)
            Dim failedLockers As New List(Of String)

            For Each lockerNumber In reservedLockers
                If myEpoch <> _uiEpoch Then Return

                Try
                    Dim authorizeResponse = Await AuthorizeLockerOpenActionForWorkOrderAsync(
                workOrderNumber:=wo,
                lockerNumber:=lockerNumber,
                correlationId:=actionId,
                sessionUserId:=If(_authResult?.UserId, ""),
                ct:=CancellationToken.None)

                    If myEpoch <> _uiEpoch Then Return

                    If authorizeResponse Is Nothing OrElse
               authorizeResponse.authorization Is Nothing OrElse
               Not authorizeResponse.authorization.isAuthorized Then

                        failedLockers.Add(lockerNumber)
                    Else
                        Dim opened = TryOpenLockerWithAudit(actionId, wo, lockerNumber)

                        If opened Then
                            openedLockers.Add(lockerNumber)

                            Await AckLockerActionSafeAsync(
                        authorizeResponse:=authorizeResponse,
                        correlationId:=actionId,
                        compartmentIds:=New List(Of String) From {lockerNumber},
                        ackStatus:="executed",
                        hardwareEventCode:="LOCKER_OPEN_OK",
                        message:=$"Locker {lockerNumber} opened for pickup.",
                        ct:=CancellationToken.None)
                        Else
                            failedLockers.Add(lockerNumber)

                            Await AckLockerActionSafeAsync(
                        authorizeResponse:=authorizeResponse,
                        correlationId:=actionId,
                        compartmentIds:=New List(Of String) From {lockerNumber},
                        ackStatus:="failed",
                        hardwareEventCode:="LOCKER_OPEN_FAILED",
                        message:=$"Locker {lockerNumber} failed to open for pickup.",
                        ct:=CancellationToken.None)
                        End If
                    End If

                Catch ex As Exception
                    failedLockers.Add(lockerNumber)
                    TraceToFile($"PICKUP_HYBRID_FAIL: corr={actionId} wo={wo} locker={lockerNumber} ex={ex.GetType().Name}:{ex.Message}")
                End Try

                Await Task.Delay(800)
                If myEpoch <> _uiEpoch Then Return
            Next

            If openedLockers.Count > 0 Then
                CompletePickupForLockers(wo, openedLockers)
            End If

            If failedLockers.Count = 0 Then
                ShowPrompt($"Locker(s) {String.Join(", ", openedLockers)} opened. Pickup completed.")
            ElseIf openedLockers.Count > 0 Then
                ShowPrompt($"Some lockers opened. Completed: {String.Join(", ", openedLockers)}. Failed: {String.Join(", ", failedLockers)}")
            Else
                ShowPrompt("Unable to open assigned locker(s). Please contact attendant.")
            End If

            Await Task.Delay(1800)
            If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
        End Function
        Private Async Sub SubmitWorkOrder(rawWorkOrder As String, source As String)

            Dim myEpoch As Integer = _uiEpoch

            Dim wo As String = (If(rawWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

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
                        Await ProcessPickupWorkOrderAsync(wo, source, actionId, myEpoch)
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

            Catch ex As Exception
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

            Return Await _backend.AuthorizeAsync(scanValue, purpose, source, CancellationToken.None)
        End Function
        Private Async Function TryReserveLockerAsync(
    workOrderNumber As String,
    requestedLockerNumber As String
) As Task(Of Boolean)

            Return Await _backend.ReserveLockerAsync(
        workOrderNumber,
        requestedLockerNumber,
        _sessionToken,
        CancellationToken.None
    )
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

            If Not ValidateLockerSizeSelection(code) Then Return
            If Not ValidateDeliveryWorkOrder() Then Return

            Dim wo As String = _activeWorkOrder.WorkOrderNumber

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            HideLockerSizeSelection()

            Dim endSessionAfterDelay As Boolean = False
            Dim errorMessage As String = Nothing

            Try
                ShowPrompt($"Finding an available {code} compartment…")

                Dim assignedLockerNumber As String = _assigner.SelectNextAvailableLockerNumber(code)

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then
                    AuditNoAvailability(actionId, wo, code)
                    ShowPrompt($"No {code} compartments are available. Select a different size.")

                    Await Task.Delay(1200)
                    If myEpoch <> _uiEpoch Then Return

                    _state = ScreenState.AwaitLockerSize
                    ShowLockerSizeSelection($"No {code} compartments are available. Select a different size.")
                    Return
                End If

                ReserveLockerForDelivery(
            lockerNumber:=assignedLockerNumber,
            workOrderNumber:=wo,
            sizeCode:=code,
            correlationId:=actionId
        )

                AuditAssignSucceeded(actionId, wo, code, assignedLockerNumber)

                ShowPrompt($"Authorizing locker {assignedLockerNumber}…")

                Dim authorizeResponse As LockerAuthorizeResponseDto =
            Await AuthorizeLockerOpenActionForWorkOrderAsync(
                workOrderNumber:=wo,
                lockerNumber:=assignedLockerNumber,
                correlationId:=actionId,
                sessionUserId:=If(_courierAuth?.UserId, _authResult?.UserId),
                ct:=CancellationToken.None)

                If myEpoch <> _uiEpoch Then Return

                If authorizeResponse Is Nothing OrElse
           authorizeResponse.authorization Is Nothing OrElse
           Not authorizeResponse.authorization.isAuthorized Then

                    ReleaseLockerReservation(assignedLockerNumber, "DeliveryAuthorizeDenied")
                    ShowPrompt($"Delivery was not authorized for locker {assignedLockerNumber}.")
                    endSessionAfterDelay = True
                Else
                    ShowPrompt($"Opening locker {assignedLockerNumber}…")

                    Dim opened As Boolean = TryOpenLockerWithAudit(actionId, wo, assignedLockerNumber)

                    If opened Then
                        Await AckLockerActionSafeAsync(
                    authorizeResponse:=authorizeResponse,
                    correlationId:=actionId,
                    compartmentIds:=New List(Of String) From {assignedLockerNumber},
                    ackStatus:="executed",
                    hardwareEventCode:="LOCKER_OPEN_OK",
                    message:=$"Locker {assignedLockerNumber} opened for delivery.",
                    ct:=CancellationToken.None)

                        ShowPrompt($"Locker {assignedLockerNumber} opened. Place contents inside and close the door.")

                        Dim closedOk = Await WaitForLockerClosedAsync(
                    lockerNumber:=assignedLockerNumber,
                    timeoutMs:=120000,
                    myEpoch:=myEpoch)

                        If myEpoch <> _uiEpoch Then Return

                        If Not closedOk Then
                            ShowPrompt($"Please close locker {assignedLockerNumber} to continue.")
                            endSessionAfterDelay = True
                        Else
                            CompleteDeliveryForLocker(wo, assignedLockerNumber)

                            Dim deliverAnother = Await PromptDeliverAnotherAsync(
                        timeoutSeconds:=15,
                        myEpoch:=myEpoch)

                            If myEpoch <> _uiEpoch Then Return

                            If deliverAnother.HasValue Then
                                If deliverAnother.Value Then
                                    ContinueDeliverySession()
                                    Return
                                Else
                                    EndDeliverySession()
                                    Return
                                End If
                            Else
                                EndDeliverySession()
                                Return
                            End If
                        End If

                    Else
                        Await AckLockerActionSafeAsync(
                    authorizeResponse:=authorizeResponse,
                    correlationId:=actionId,
                    compartmentIds:=New List(Of String) From {assignedLockerNumber},
                    ackStatus:="failed",
                    hardwareEventCode:="LOCKER_OPEN_FAILED",
                    message:=$"Locker {assignedLockerNumber} failed to open for delivery.",
                    ct:=CancellationToken.None)

                        ReleaseLockerReservation(assignedLockerNumber, "DeliveryOpenFailed")
                        endSessionAfterDelay = True
                    End If
                End If

            Catch ex As Exception
                If myEpoch <> _uiEpoch Then Return
                errorMessage = $"System unavailable: {ex.Message}"
                endSessionAfterDelay = True

            Finally
                If myEpoch = _uiEpoch Then
                    FocusHidSink()
                End If
            End Try

            If myEpoch <> _uiEpoch Then Return

            If Not String.IsNullOrWhiteSpace(errorMessage) Then
                ShowPrompt(errorMessage)
            End If

            If endSessionAfterDelay Then
                Await Task.Delay(1500)
                If myEpoch = _uiEpoch Then EndDeliverySession()
            End If

        End Sub
        Private Function ValidateLockerSizeSelection(code As String) As Boolean

            If code.Length = 0 Then
                ShowPrompt("No size code received.")
                Return False
            End If

            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then
                ShowPrompt("Selection ignored: debounce active.")
                Return False
            End If
            _lastSubmitUtc = now

            If _state = ScreenState.ValidatingCredential Then
                ShowPrompt("Selection ignored: already validating.")
                Return False
            End If

            If Not _selectedWorkflow.HasValue OrElse _selectedWorkflow.Value <> WorkflowMode.Delivery Then
                ShowPrompt("Size selection is only used for Delivery.")
                Return False
            End If

            Return True

        End Function
        Private Function ValidateDeliveryWorkOrder() As Boolean

            If _activeWorkOrder Is Nothing OrElse String.IsNullOrWhiteSpace(_activeWorkOrder.WorkOrderNumber) Then
                ShowPrompt("Scan a Work Order first.")
                _state = ScreenState.AwaitWorkOrder
                Return False
            End If

            Return True

        End Function

        Private Sub ReserveLockerForDelivery(lockerNumber As String,
                                     workOrderNumber As String,
                                     sizeCode As String,
                                     correlationId As String)

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
                    Include(Function(l) l.Status).
                    SingleOrDefault(Function(x) x.LockerNumber = lockerNumber)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {lockerNumber} was not found.")
                End If

                If locker.Status Is Nothing Then
                    Throw New InvalidOperationException($"Locker {lockerNumber} does not have a LockerStatus row.")
                End If

                locker.Status.OccupancyState = OccupancyState.Reserved
                locker.Status.ReservedWorkOrderNumber = workOrderNumber
                locker.Status.ReservedCorrelationId = correlationId
                locker.Status.LastUpdatedUtc = DateTime.UtcNow

                db.SaveChanges()

            End Using

        End Sub
        Private Sub ShowPrompt(text As String)
            If text = "Select Pickup or Delivery" OrElse text = "Enter Admin Credential" Then
                TraceToFile("SHOWPROMPT: " & text)
            End If

            UserPromptText.Text = text
            SafeFadeIn()
        End Sub

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
                TraceToFile($"COMMIT_SAFE_FAIL: corr={cid} wo={wo} size={sc} locker={ln} ex={ex.GetType().Name}:{ex.Message}")

                Try
                    Dim store As New PendingCommitStore()
                    store.Enqueue(New PendingDeliveryCommit With {
            .CommitId = Guid.NewGuid().ToString("N"),
            .RequestId = cid,
            .KioskId = AppSettings.KioskID,
            .LocationId = AppSettings.LocationId,
            .WorkOrderNumber = wo,
            .LockerNumber = ln,
            .SizeCode = sc,
            .SessionUserId = If(courierAuth?.UserId, ""),
            .CreatedUtc = DateTime.UtcNow,
            .AttemptCount = 0,
            .NextAttemptUtc = DateTime.UtcNow.AddSeconds(15),
            .LastError = ex.Message
        })
                Catch qex As Exception
                    TraceToFile($"COMMIT_ENQUEUE_FAIL: corr={cid} ex={qex.GetType().Name}:{qex.Message}")
                End Try

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

            Dim dto As New DeliveryCommitRequestDto With {
        .requestId = If(String.IsNullOrWhiteSpace(requestId), Guid.NewGuid().ToString("N"), requestId),
        .timestampUtc = DateTime.UtcNow.ToString("o"),
        .kioskId = AppSettings.KioskID,
        .locationId = AppSettings.LocationId,
        .workOrderNumber = workOrderNumber,
        .lockerNumber = lockerNumber,
        .sizeCode = sizeCode,
        .courierUserID = If(courierAuth IsNot Nothing, courierAuth.UserId, Nothing)
    }

            Await _backend.CommitDeliveryAsync(dto, _sessionToken, CancellationToken.None)
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

                ' Get only size codes that actually exist in commissioned/enabled lockers
                Dim commissionedSizeCodes = db.Lockers.AsNoTracking().
            Where(Function(l) l.IsEnabled).
            Select(Function(l) l.SizeCode).
            Distinct().
            ToList()

                If commissionedSizeCodes Is Nothing OrElse commissionedSizeCodes.Count = 0 Then
                    Return New List(Of LockerSize)()
                End If

                ' Return only size definitions for those commissioned locker sizes
                Return db.LockerSizes.AsNoTracking().
            Where(Function(s) s.IsEnabled AndAlso commissionedSizeCodes.Contains(s.SizeCode)).
            OrderBy(Function(s) s.SortOrder).
            ThenBy(Function(s) s.SizeCode).
            ToList()

            End Using
        End Function
        Private Sub EnsureSizeTilesLoaded()
            _lockerSizes = LoadLockerSizesFromDb()
            _sizeTiles = New List(Of SizeTile)()

            If _lockerSizes Is Nothing OrElse _lockerSizes.Count = 0 Then
                Return
            End If

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
            If btn Is Nothing Then
                ShowPrompt("Size click failed: sender was not a Button.")
                Return
            End If

            Dim code = TryCast(btn.Tag, String)
            If String.IsNullOrWhiteSpace(code) Then
                ShowPrompt("Size click failed: no SizeCode bound to button.")
                Return
            End If

            ShowPrompt($"Selected size {code}.")
            SelectSizeAndAssignLocker(code)
        End Sub
        Private Sub ShowLockerSizeSelection(Optional promptText As String = "Select compartment size.")
            EnsureSizeTilesLoaded()
            BindSizeTilesToUi()

            _selectedSizeCode = Nothing

            If _sizeTiles Is Nothing OrElse _sizeTiles.Count = 0 Then
                SizeSelectionPanel.Visibility = Visibility.Collapsed
                _state = ScreenState.AwaitWorkOrder
                ShowPrompt("No commissioned compartment sizes are available.")
                Return
            End If

            SizeSelectionPanel.Visibility = Visibility.Visible
            _state = ScreenState.AwaitLockerSize

            ShowPrompt(promptText)
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

        Private Async Function WaitForLockerClosedAsync(lockerNumber As String,
                                                timeoutMs As Integer,
                                                myEpoch As Integer) As Task(Of Boolean)

            Dim startedUtc = DateTime.UtcNow

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            AsNoTracking().
            SingleOrDefault(Function(l) l.LockerNumber = lockerNumber)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {lockerNumber} was not found.")
                End If

                Do
                    If myEpoch <> _uiEpoch Then Return False

                    Dim isOpen As Boolean = False
                    Dim gotState As Boolean = False

                    Try
                        gotState = _lockerController.TryGetLockOpen(locker.Branch, locker.RelayId, isOpen)
                    Catch
                        gotState = False
                    End Try

                    If gotState AndAlso Not isOpen Then
                        Return True
                    End If

                    If (DateTime.UtcNow - startedUtc).TotalMilliseconds >= timeoutMs Then
                        Return False
                    End If

                    Await Task.Delay(250)
                Loop

            End Using

        End Function
        Private Sub ShowDeliverAnotherPrompt(timeoutSeconds As Integer)
            ShowPrompt($"Deliver another? Returning to start in {timeoutSeconds} seconds.")

            If DeliverAnotherCountdownText IsNot Nothing Then
                DeliverAnotherCountdownText.Text = $"Returning to start in {timeoutSeconds} seconds."
            End If

            If DeliverAnotherPanel IsNot Nothing Then
                DeliverAnotherPanel.Visibility = Visibility.Visible
            End If

            If DeliverAnotherYesButton IsNot Nothing Then
                DeliverAnotherYesButton.IsEnabled = True
                DeliverAnotherYesButton.Visibility = Visibility.Visible
            End If

            If DeliverAnotherNoButton IsNot Nothing Then
                DeliverAnotherNoButton.IsEnabled = True
                DeliverAnotherNoButton.Visibility = Visibility.Visible
            End If
        End Sub
        Private Sub HideDeliverAnotherPrompt()
            If DeliverAnotherPanel IsNot Nothing Then
                DeliverAnotherPanel.Visibility = Visibility.Collapsed
            End If

            If DeliverAnotherYesButton IsNot Nothing Then
                DeliverAnotherYesButton.IsEnabled = False
                DeliverAnotherYesButton.Visibility = Visibility.Collapsed
            End If

            If DeliverAnotherNoButton IsNot Nothing Then
                DeliverAnotherNoButton.IsEnabled = False
                DeliverAnotherNoButton.Visibility = Visibility.Collapsed
            End If
        End Sub
        Private Async Function PromptDeliverAnotherAsync(timeoutSeconds As Integer,
                                                 myEpoch As Integer) As Task(Of Boolean?)

            HideDeliverAnotherPrompt()

            _deliverAnotherTcs = New TaskCompletionSource(Of Boolean?)(
        TaskCreationOptions.RunContinuationsAsynchronously)

            If _deliverAnotherTimeoutCts IsNot Nothing Then
                _deliverAnotherTimeoutCts.Cancel()
                _deliverAnotherTimeoutCts.Dispose()
            End If

            _deliverAnotherTimeoutCts = New CancellationTokenSource()
            Dim token = _deliverAnotherTimeoutCts.Token

            ShowDeliverAnotherPrompt(timeoutSeconds)

            Dim timeoutTask As Task = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), token)
            Dim completedTask = Await Task.WhenAny(_deliverAnotherTcs.Task, timeoutTask)

            If myEpoch <> _uiEpoch Then
                HideDeliverAnotherPrompt()
                Return Nothing
            End If

            Dim result As Boolean? = Nothing

            If completedTask Is _deliverAnotherTcs.Task Then
                _deliverAnotherTimeoutCts.Cancel()
                result = Await _deliverAnotherTcs.Task
            Else
                result = Nothing
            End If

            HideDeliverAnotherPrompt()
            Return result

        End Function
        Private Sub ContinueDeliverySession()
            HideDeliverAnotherPrompt()

            _activeWorkOrder = Nothing
            _state = ScreenState.AwaitWorkOrder

            KeypadControl.Reset()
            SetUiEnabled(True)
            FocusHidSink()

            ShowPrompt("Scan or enter next work order.")
        End Sub
        Private Sub EndDeliverySession()
            HideDeliverAnotherPrompt()

            If _deliverAnotherTimeoutCts IsNot Nothing Then
                _deliverAnotherTimeoutCts.Cancel()
                _deliverAnotherTimeoutCts.Dispose()
                _deliverAnotherTimeoutCts = Nothing
            End If

            _deliverAnotherTcs = Nothing

            ResetToAwaitWorkflowChoice()
        End Sub
        Private Sub DeliverAnotherYesButton_Click(sender As Object, e As RoutedEventArgs) Handles DeliverAnotherYesButton.Click
            If _deliverAnotherTcs IsNot Nothing Then
                _deliverAnotherTcs.TrySetResult(True)
            End If
        End Sub

        Private Sub DeliverAnotherNoButton_Click(sender As Object, e As RoutedEventArgs) Handles DeliverAnotherNoButton.Click
            If _deliverAnotherTcs IsNot Nothing Then
                _deliverAnotherTcs.TrySetResult(False)
            End If
        End Sub
        Private Sub CompletePickupForLockers(workOrderNumber As String, lockerNumbers As IEnumerable(Of String))

            Dim wo = (If(workOrderNumber, "")).Trim()
            Dim lockerList = If(lockerNumbers, Enumerable.Empty(Of String)()).
                Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
                Select(Function(x) x.Trim()).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()

            If lockerList.Count = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim lockers = db.Lockers.
                    Include(Function(l) l.Status).
                    Where(Function(l) lockerList.Contains(l.LockerNumber)).
                    ToList()

                For Each locker In lockers
                    If locker.Status Is Nothing Then
                        Continue For
                    End If

                    locker.Status.OccupancyState = OccupancyState.Vacant
                    locker.Status.PackagePresent = False
                    locker.Status.LastUpdatedUtc = DateTime.UtcNow

                    locker.Status.LastWorkOrderNumber = wo
                    locker.Status.ReservedWorkOrderNumber = Nothing
                    locker.Status.ReservedCorrelationId = Nothing
                    locker.Status.ReservedUntilUtc = Nothing

                    locker.Status.LastReason = "PickupCompleted"
                    locker.Status.LastActorId = If(_authResult IsNot Nothing, _authResult.UserId, Nothing)
                Next

                db.SaveChanges()

            End Using

        End Sub

        Private Async Function AuthorizeLockerOpenActionForWorkOrderAsync(
    workOrderNumber As String,
    lockerNumber As String,
    correlationId As String,
    sessionUserId As String,
    ct As CancellationToken
) As Task(Of LockerAuthorizeResponseDto)

            Dim wo = (If(workOrderNumber, "")).Trim()
            Dim ln = (If(lockerNumber, "")).Trim()
            Dim userId = (If(sessionUserId, "")).Trim()
            Dim corr = (If(correlationId, "")).Trim()

            If wo.Length = 0 Then Throw New ArgumentException("workOrderNumber is required.", NameOf(workOrderNumber))
            If ln.Length = 0 Then Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
            If userId.Length = 0 Then Throw New InvalidOperationException("No authorized session user is available.")
            If corr.Length = 0 Then corr = Guid.NewGuid().ToString("N")

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
                    AsNoTracking().
                    SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {ln} was not found.")
                End If

                Dim dto As New LockerAuthorizeRequestDto With {
                    .requestId = Guid.NewGuid().ToString("N"),
                    .correlationId = corr,
                    .requestedBy = userId,
                    .requestedByType = "user",
                    .siteCode = If(String.IsNullOrWhiteSpace(AppSettings.LocationId), "UNKNOWN-SITE", AppSettings.LocationId),
                    .lockerBankId = $"BANK-{locker.Branch}",
                    .lockerId = locker.LockerNumber,
                    .doorId = $"D{locker.RelayId}",
                    .actionType = "open_door",
                    .requestedAtUtc = DateTime.UtcNow.ToString("o"),
                    .reasonCode = "WORK_ORDER",
                    .metadata = New LockerAuthorizeMetadataDto With {
                        .workOrderId = wo
                    }
                }

                Return Await _backend.AuthorizeLockerActionAsync(dto, _sessionToken, ct)

            End Using

        End Function
        Private Async Function AckLockerActionSafeAsync(
            authorizeResponse As LockerAuthorizeResponseDto,
            correlationId As String,
            compartmentIds As IEnumerable(Of String),
            ackStatus As String,
            hardwareEventCode As String,
            message As String,
            ct As CancellationToken
        ) As Task(Of Boolean)

            If authorizeResponse Is Nothing Then Return False
            If String.IsNullOrWhiteSpace(authorizeResponse.transactionId) Then Return False
            If String.IsNullOrWhiteSpace(authorizeResponse.commandId) Then Return False

            Dim ids = If(compartmentIds, Enumerable.Empty(Of String)()).
                Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
                Select(Function(x) x.Trim()).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()

            Dim dto As New LockerAckRequestDto With {
                .transactionId = authorizeResponse.transactionId,
                .commandId = authorizeResponse.commandId,
                .correlationId = If(String.IsNullOrWhiteSpace(correlationId), Guid.NewGuid().ToString("N"), correlationId),
                .ackStatus = ackStatus,
                .adapterName = AppSettings.AdapterName,
                .hardwareEventCode = hardwareEventCode,
                .message = message,
                .compartmentIds = ids
            }

            Try
                Await _backend.AckLockerActionAsync(dto, _sessionToken, ct)
                Return True
            Catch ex As Exception
                TraceToFile($"ACK_FAIL: txn={dto.transactionId} cmd={dto.commandId} corr={dto.correlationId} status={dto.ackStatus} ex={ex.GetType().Name}:{ex.Message}")
                Return False
            End Try

        End Function
        Private Sub ReleaseLockerReservation(lockerNumber As String, reason As String)

            Dim ln = (If(lockerNumber, "")).Trim()
            If ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
                    Include(Function(l) l.Status).
                    SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Return
                End If

                locker.Status.OccupancyState = OccupancyState.Vacant
                locker.Status.PackagePresent = False
                locker.Status.ReservedWorkOrderNumber = Nothing
                locker.Status.ReservedCorrelationId = Nothing
                locker.Status.ReservedUntilUtc = Nothing
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = reason

                db.SaveChanges()

            End Using

        End Sub
        Private Sub CompleteDeliveryForLocker(workOrderNumber As String, lockerNumber As String)

            Dim wo = (If(workOrderNumber, "")).Trim()
            Dim ln = (If(lockerNumber, "")).Trim()

            If wo.Length = 0 OrElse ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
                    Include(Function(l) l.Status).
                    SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Return
                End If

                locker.Status.OccupancyState = OccupancyState.Occupied
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastWorkOrderNumber = wo
                locker.Status.LastReason = "DeliveryCompleted"
                locker.Status.LastActorId = If(_courierAuth IsNot Nothing, _courierAuth.UserId, Nothing)

                db.SaveChanges()

            End Using

        End Sub


    End Class
End Namespace



