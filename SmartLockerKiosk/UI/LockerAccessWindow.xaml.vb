Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop
Imports System.Windows.Media
Imports System.Windows.Media.Animation
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk
    Partial Public Class LockerAccessWindow
        'Revision 1.00
        Inherits Window
        Private fadeIn As Storyboard
        ' Authorization result from cloud
        Private Class AuthProfile
            Public Property IsAuthenticated As Boolean
            Public Property IsAdmin As Boolean
            Public Property CanPickup As Boolean
            Public Property CanDeliver As Boolean
            Public Property SessionToken As String
            Public Property WorkOrders As List(Of WorkOrderAuthItem) =
    New List(Of WorkOrderAuthItem)()


        End Class
        Public Class WorkOrderAuthItem
            Public Property WorkOrderNumber As String
            Public Property TransactionType As String  ' "Pickup" / "Delivery" / etc.
            Public Property LockerNumber As String     ' null/empty for Delivery when not assigned
            Public Property AllowedSizeCode As String  ' optional
        End Class
        Private Class SizeTile
            Public Property SizeCode As String
            Public Property DisplayName As String
            Public Property DimText As String        ' "10W × 6H × 14D"
            Public Property ThumbWidth As Double     ' for rectangle
            Public Property ThumbHeight As Double    ' for rectangle
        End Class
        Public Class AuthResult
            Public Property IsAuthorized As Boolean
            Public Property Message As String

            ' What the kiosk should do next
            Public Property Purpose As AuthPurpose
            Public Property NextAction As String   ' e.g. "OPEN_LOCKER", "PROMPT_WORK_ORDER", "SELECT_LOCKER"

            ' Common identity fields (optional)
            Public Property UserId As String
            Public Property DisplayName As String

            ' Pickup / DayUse: if the server already knows/assigns a locker
            Public Property LockerNumber As String    ' alphanumeric; Nothing/"" means not assigned

            ' Pickup: optional but useful for logging
            Public Property WorkOrderNumber As String
            Public Property WorkOrders As List(Of WorkOrderAuthItem) = New List(Of WorkOrderAuthItem)()
        End Class

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
        Public Enum AuthPurpose
            PickupAccess
            DeliveryCourierAuth
            DayUseStart
            AdminAccess
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


        Public Sub New()
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)
        End Sub
        Private Sub LockerAccessWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
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
                            _lockerController.UnlockByLockerNumber(match.LockerNumber)
                            UserPromptText.Text = $"Locker {match.LockerNumber} opened."
                            SafeFadeIn()
                        Catch
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
            ' --- normalize input ---
            Dim credential As String = (If(rawCredential, "")).Trim()

            ' Empty submit should NOT consume debounce window
            If credential.Length = 0 Then
                Return
            End If

            ' --- debounce (handles keypad double-fire etc.) ---
            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then
                Return
            End If
            _lastSubmitUtc = now

            ' --- state guards ---
            If _state = ScreenState.ValidatingCredential Then
                Return
            End If

            If _state <> ScreenState.AwaitCredential AndAlso _state <> ScreenState.AwaitAdminCredential Then
                Return
            End If

            ' IMPORTANT: capture admin-vs-normal BEFORE changing _state
            Dim isAdminFlow As Boolean = (_state = ScreenState.AwaitAdminCredential)

            ' For normal (non-admin) flow, we must have a selected workflow
            If Not isAdminFlow AndAlso Not _selectedWorkflow.HasValue Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            ' Determine purpose
            Dim purpose As AuthPurpose =
        If(isAdminFlow, AuthPurpose.AdminAccess, GetPurposeForWorkflow(_selectedWorkflow.Value))

            ' --- transition to validating ---
            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            UserPromptText.Text = "Validating…"
            SafeFadeIn()

            Try
                Dim result As AuthResult = Await ValidateCredentialWithServerAsync(credential, purpose)

                If result Is Nothing OrElse Not result.IsAuthorized Then
                    ' Stay in credential prompt for retry (admin stays admin; normal stays normal)
                    _state = If(isAdminFlow, ScreenState.AwaitAdminCredential, ScreenState.AwaitCredential)
                    UserPromptText.Text = If(result?.Message, "Credential not recognized")
                    SafeFadeIn()
                    Return
                End If

                _authResult = result
                _authorizedWorkOrders = If(result.WorkOrders, New List(Of WorkOrderAuthItem)())
                _activeWorkOrder = Nothing


                ' ===== ADMIN SHORT-CIRCUIT =====
                If purpose = AuthPurpose.AdminAccess Then
                    ShowAdminPanel(Me, EventArgs.Empty)
                    ResetToAwaitWorkflowChoice()
                    Return
                End If
                ' ===============================

                ' Route to the correct workflow continuation
                Await ContinueWorkflowAfterAuthAsync(_selectedWorkflow.Value, result)

            Catch ex As Exception
                ResetToAwaitWorkflowChoice()
                UserPromptText.Text = "System unavailable"
                SafeFadeIn()

            Finally
                ' Always restore UI to an interactive state; your state machine decides what is enabled next.
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
            End Try
        End Sub
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
            PromptForCredential()
        End Sub
        Private Sub DeliverButton_Click(sender As Object, e As RoutedEventArgs) Handles DeliverButton.Click
            If _state <> ScreenState.AwaitWorkflowChoice Then Return
            _selectedWorkflow = WorkflowMode.Delivery
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
            FocusHidSink()
        End Sub
        Private Sub ShowAdminPanel(sender As Object, e As EventArgs)
            Dim adminScreen As New AdminScreen With {.Owner = Me}
            adminScreen.ShowDialog()
            FocusHidSink()
        End Sub
        Private Sub ShowLockerSizeSelection()
            SizeSelectionPanel.Visibility = Visibility.Visible
            ' Populate tiles here if you haven’t already
            _state = ScreenState.AwaitLockerSize
            UserPromptText.Text = "Select compartment size."
            SafeFadeIn()
        End Sub
        Private Sub HideLockerSizeSelection()
            SizeSelectionPanel.Visibility = Visibility.Collapsed
        End Sub
        Private Sub SizeTile_Click(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            If btn Is Nothing Then Return

            Dim code = TryCast(btn.Tag, String)
            If String.IsNullOrWhiteSpace(code) Then Return

            ' Kick off assignment/open
            SelectSizeAndAssignLocker(code)
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
        Private Async Sub SelectSizeAndAssignLocker(sizeCode As String)
            Dim code = (If(sizeCode, "")).Trim().ToUpperInvariant()
            If code.Length = 0 Then Return

            ' Guards
            If Not _selectedWorkflow.HasValue OrElse _selectedWorkflow.Value <> WorkflowMode.Delivery Then
                UserPromptText.Text = "Size selection is only used for Delivery."
                SafeFadeIn()
                Return
            End If

            If _activeWorkOrder Is Nothing OrElse String.IsNullOrWhiteSpace(_activeWorkOrder.WorkOrderNumber) Then
                UserPromptText.Text = "Scan a Work Order first."
                SafeFadeIn()
                _state = ScreenState.AwaitWorkOrder
                Return
            End If

            ' Debounce double-clicks
            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

            If _state = ScreenState.ValidatingCredential Then Return

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            Dim shouldDelayThenReset As Boolean = False
            Dim shouldReturnToSizeSelection As Boolean = False

            Try
                HideLockerSizeSelection()

                UserPromptText.Text = $"Finding an available {code} compartment…"
                SafeFadeIn()

                ' 1) Ask cloud for an available locker
                Dim assignedLockerNumber As String =
            Await AssignDeliveryLockerWithServerAsync(_activeWorkOrder.WorkOrderNumber, code, _courierAuth)

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then
                    UserPromptText.Text = $"No {code} compartments are available. Select a different size."
                    SafeFadeIn()

                    shouldReturnToSizeSelection = True
                    Return
                End If

                ' 2) Open locker (no await here)
                UserPromptText.Text = $"Opening locker {assignedLockerNumber}…"
                SafeFadeIn()

                Try
                    _lockerController.UnlockByLockerNumber(assignedLockerNumber)
                Catch
                    UserPromptText.Text = $"Unable to open locker {assignedLockerNumber}"
                    SafeFadeIn()

                    shouldDelayThenReset = True
                    Return
                End Try

                ' 3) Post transaction to cloud
                UserPromptText.Text = $"Locker {assignedLockerNumber} opened. Logging delivery…"
                SafeFadeIn()

                Await PostDeliveryTransactionToServerAsync(
            workOrderNumber:=_activeWorkOrder.WorkOrderNumber,
            sizeCode:=code,
            lockerNumber:=assignedLockerNumber,
            courierAuth:=_courierAuth
        )

                UserPromptText.Text = $"Delivery started. Locker {assignedLockerNumber} opened."
                SafeFadeIn()

                shouldDelayThenReset = True

            Catch ex As Exception
                ' No awaits here
                UserPromptText.Text = "System unavailable"
                SafeFadeIn()
                shouldDelayThenReset = True

            Finally
                ' No awaits here
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
            End Try

            ' ===== VB rule: awaits must be AFTER Try/Catch/Finally =====
            If shouldReturnToSizeSelection Then
                _state = ScreenState.AwaitLockerSize
                ShowLockerSizeSelection()
                Return
            End If

            If shouldDelayThenReset Then
                Await Task.Delay(1500)
                ResetToAwaitWorkflowChoice()
                Return
            End If
        End Sub

        Private Async Function AssignDeliveryLockerWithServerAsync(
    workOrderNumber As String,
    sizeCode As String,
    courierAuth As AuthResult
) As Task(Of String)

            Await Task.Delay(150) ' simulate latency

            ' TODO: replace with real call
            ' For now: return a deterministic locker based on size
            Select Case (If(sizeCode, "")).Trim().ToUpperInvariant()
                Case "A" : Return "A-001"
                Case "B" : Return "A-002"
                Case "C" : Return "A-003"
                Case "D" : Return "A-004"
                Case Else : Return ""
            End Select
        End Function
        Private Async Function PostDeliveryTransactionToServerAsync(
    workOrderNumber As String,
    sizeCode As String,
    lockerNumber As String,
    courierAuth As AuthResult
) As Task

            Await Task.Delay(100) ' simulate latency

            ' TODO: replace with real call
            ' You’ll likely post:
            ' - workOrderNumber
            ' - lockerNumber
            ' - sizeCode
            ' - kiosk id / location
            ' - courier identity (courierAuth.UserId / SessionToken)
            ' - timestamp, etc.
        End Function

        Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub
        Private Async Function ValidateCredentialWithServerAsync(
    scanValue As String,
    purpose As AuthPurpose
) As Task(Of AuthResult)
            ' ====== TEMP STUB FOR TESTING CORE FLOW ======
            ' Replace with real HTTP later.
            Await Task.Delay(150) ' simulate latency

            Dim scan = (If(scanValue, "")).Trim()

            Select Case purpose
                Case AuthPurpose.PickupAccess
                    ' Example: scan "1111" -> authorized pickup -> locker "A-001"
                    If scan = "123698740" OrElse scan.Equals("PICKUP", StringComparison.OrdinalIgnoreCase) Then
                        Return New AuthResult With {
                            .IsAuthorized = True,
                            .Purpose = purpose,
                            .Message = "OK",
                            .UserId = "USER1",
                            .DisplayName = "Test Pickup User",
                            .WorkOrderNumber = "WO-10001",
                            .LockerNumber = "A-001",
                            .NextAction = "OPEN_LOCKER"
                        }

                    End If

                    Return New AuthResult With {
    .IsAuthorized = True,
    .Purpose = purpose,
    .Message = "OK",
    .UserId = "USER1",
    .DisplayName = "Test Pickup User",
    .NextAction = "PROMPT_WORK_ORDER",
    .WorkOrders = New List(Of WorkOrderAuthItem) From {
        New WorkOrderAuthItem With {
            .WorkOrderNumber = "WO-10001",
            .TransactionType = "Pickup",
            .LockerNumber = "A-001",
            .AllowedSizeCode = "A"
        }
    }
}


                Case AuthPurpose.DeliveryCourierAuth
                    ' Example: scan "2222" -> authorized courier
                    If scan = "2222" OrElse scan.Equals("COURIER", StringComparison.OrdinalIgnoreCase) Then
                        Return New AuthResult With {
                            .IsAuthorized = True,
                            .Purpose = purpose,
                            .Message = "OK",
                            .UserId = "COURIER1",
                            .DisplayName = "Test Courier",
                            .NextAction = "PROMPT_WORK_ORDER"
                        }
                    End If

                    Return New AuthResult With {
    .IsAuthorized = True,
    .Purpose = purpose,
    .Message = "OK",
    .UserId = "COURIER1",
    .DisplayName = "Test Courier",
    .NextAction = "PROMPT_WORK_ORDER",
    .WorkOrders = New List(Of WorkOrderAuthItem) From {
        New WorkOrderAuthItem With {
            .WorkOrderNumber = "WO-20001",
            .TransactionType = "Delivery",
            .LockerNumber = "",          ' not assigned yet
            .AllowedSizeCode = "B"
        }
    }
}

                Case AuthPurpose.AdminAccess
                    ' Example admin credentials
                    ' - keypad: "9999"
                    ' - or badge/scan: "ADMIN"
                    If scan = "123698740" OrElse scan.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) Then
                        Return New AuthResult With {
            .IsAuthorized = True,
            .Purpose = purpose,
            .Message = "OK",
            .UserId = "ADMIN1",
            .DisplayName = "Test Admin",
            .NextAction = "OPEN_ADMIN"
        }
                    End If

                    Return New AuthResult With {
        .IsAuthorized = False,
        .Purpose = purpose,
        .Message = "Admin credential not recognized."
    }


                Case Else
                    Return New AuthResult With {
                        .IsAuthorized = False,
                        .Purpose = purpose,
                        .Message = "Unsupported purpose in stub."
                    }
            End Select

        End Function
    End Class
End Namespace



