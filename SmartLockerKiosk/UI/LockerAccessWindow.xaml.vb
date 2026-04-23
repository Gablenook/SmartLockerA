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

        Private ReadOnly _barcodeScanService As BarcodeScanService

        Private _activeAssetTag As String = Nothing
        Private _isDefectiveReturn As Boolean = False
        Private _selectedDefectType As String = Nothing

        Private _compartmentTiles As List(Of CompartmentTile) = New List(Of CompartmentTile)()
        Private _selectedCompartmentNumber As String = Nothing

        Private _activeWorkflow As WorkflowDefinition = Nothing
        Private _activeStep As WorkflowStepDefinition = Nothing
        Private _workflowConfig As KioskWorkflowConfiguration = Nothing
        Private _scanValidationProfiles As Dictionary(Of String, ScanValidationProfile) =
    New Dictionary(Of String, ScanValidationProfile)(StringComparer.OrdinalIgnoreCase)
        Private _workflowDefinitions As Dictionary(Of String, WorkflowDefinition) =
    New Dictionary(Of String, WorkflowDefinition)(StringComparer.OrdinalIgnoreCase)

#Region "Initialization and Configuration Loading"
        Public Sub New(lockerController As LockerControllerService)
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)

            ApplyTheme("RYDER")

            _lockerController = lockerController

            If AppSettings.UseBackendBypass Then
                _backend = New BypassOperationsBackendService()
            Else
                _backend = New OperationsBackendService(BackendHttpFactory.CreateHttpClient())
            End If

            _barcodeScanService = New BarcodeScanService()

            _workflowConfig = LoadWorkflowConfiguration()
            IndexWorkflowConfiguration(_workflowConfig)

            ConfigureBarcodeValidation()
        End Sub
        Private Sub ApplyTheme(themeName As String)

            Dim merged = Application.Current.Resources.MergedDictionaries

            For i As Integer = merged.Count - 1 To 0 Step -1
                Dim src = merged(i).Source?.ToString()

                If src IsNot Nothing AndAlso
           (src.Contains("TsaTheme.xaml") OrElse
            src.Contains("RyderTheme.xaml")) Then
                    merged.RemoveAt(i)
                End If
            Next

            Dim themeUri As Uri

            Select Case If(themeName, "").Trim().ToUpperInvariant()
                Case "RYDER"
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/RyderTheme.xaml", UriKind.Relative)

                Case Else
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/TsaTheme.xaml", UriKind.Relative)
            End Select

            merged.Add(New ResourceDictionary With {.Source = themeUri})

        End Sub
        Private Sub ConfigureBarcodeValidation()
            _barcodeScanService.Validator =
        Function(text As String) As ScanValidationResult
            Return ValidateScanForCurrentStep(text)
        End Function
        End Sub
        Private Function ValidateScanForCurrentStep(text As String) As ScanValidationResult
            Dim value As String = If(text, String.Empty).Trim()

            If String.IsNullOrWhiteSpace(value) Then
                Return ScanValidationResult.Invalid("Scan is empty.")
            End If

            If _activeStep Is Nothing OrElse String.IsNullOrWhiteSpace(_activeStep.ValidationProfileKey) Then
                Select Case _state
                    Case ScreenState.AwaitCredential,
                 ScreenState.AwaitAdminCredential,
                 ScreenState.AwaitWorkOrder,
                 ScreenState.AwaitAssetScan
                        Return ScanValidationResult.Valid()
                    Case Else
                        Return ScanValidationResult.Invalid("Scanning is not allowed in the current state.")
                End Select
            End If

            Dim profile As ScanValidationProfile = Nothing
            If Not _scanValidationProfiles.TryGetValue(_activeStep.ValidationProfileKey.Trim(), profile) Then
                Return ScanValidationResult.Valid()
            End If

            Return ValidateAgainstProfile(value, profile)
        End Function
        Private Function ValidateAgainstProfile(value As String,
                                        profile As ScanValidationProfile) As ScanValidationResult

            Dim raw As String = If(value, String.Empty).Trim()
            Dim working As String = raw

            If String.IsNullOrWhiteSpace(raw) Then
                Return ScanValidationResult.Invalid("Scan is empty.")
            End If

            If profile Is Nothing Then
                Return ScanValidationResult.Valid()
            End If

            If Not String.IsNullOrWhiteSpace(profile.RequirePrefix) Then
                If Not working.StartsWith(profile.RequirePrefix, StringComparison.OrdinalIgnoreCase) Then
                    Return ScanValidationResult.Invalid(If(profile.RejectMessage, "Scan prefix is not valid."))
                End If

                If profile.StripPrefix Then
                    working = working.Substring(profile.RequirePrefix.Length).Trim()
                End If
            End If

            If profile.MinLength.HasValue AndAlso working.Length < profile.MinLength.Value Then
                Return ScanValidationResult.Invalid(If(profile.RejectMessage, "Scan is too short."))
            End If

            If profile.MaxLength.HasValue AndAlso working.Length > profile.MaxLength.Value Then
                Return ScanValidationResult.Invalid(If(profile.RejectMessage, "Scan is too long."))
            End If

            If Not String.IsNullOrWhiteSpace(profile.AllowedCharactersPattern) Then
                If Not System.Text.RegularExpressions.Regex.IsMatch(working, profile.AllowedCharactersPattern) Then
                    Return ScanValidationResult.Invalid(If(profile.RejectMessage, "Scan contains invalid characters."))
                End If
            End If

            Return ScanValidationResult.Valid()
        End Function
        Private Function LoadWorkflowConfiguration() As KioskWorkflowConfiguration

            Dim pickupWorkflowKey As String = If(AppSettings.HomePickupWorkflowKey, "package-retrieve").Trim()
            Dim deliveryWorkflowKey As String = If(AppSettings.HomeDeliveryWorkflowKey, "package-deposit").Trim()
            Dim assetValidationProfileKey As String = If(AppSettings.DefaultAssetValidationProfileKey, "asset_default").Trim()

            If String.IsNullOrWhiteSpace(pickupWorkflowKey) Then
                pickupWorkflowKey = "package-retrieve"
            End If

            If String.IsNullOrWhiteSpace(deliveryWorkflowKey) Then
                deliveryWorkflowKey = "package-deposit"
            End If

            If String.IsNullOrWhiteSpace(assetValidationProfileKey) Then
                assetValidationProfileKey = "asset_default"
            End If

            Dim workflows As New List(Of WorkflowDefinition) From {
        New WorkflowDefinition With {
            .WorkflowKey = "package-retrieve",
            .DisplayName = "Retrieve",
            .Mode = "package_workflow",
            .Steps = New List(Of WorkflowStepDefinition) From {
                New WorkflowStepDefinition With {
                    .StepKey = "credential_scan",
                    .Prompt = "Enter Credential",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "credential_default",
                    .NextStepKey = "work_order_scan"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "work_order_scan",
                    .Prompt = "Scan Work Order",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "workorder_default",
                    .NextStepKey = Nothing
                }
            }
        },
        New WorkflowDefinition With {
            .WorkflowKey = "package-deposit",
            .DisplayName = "Deposit",
            .Mode = "package_workflow",
            .Steps = New List(Of WorkflowStepDefinition) From {
                New WorkflowStepDefinition With {
                    .StepKey = "credential_scan",
                    .Prompt = "Enter Credential",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "credential_default",
                    .NextStepKey = "work_order_scan"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "work_order_scan",
                    .Prompt = "Scan Work Order",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "workorder_default",
                    .NextStepKey = "size_selection"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "size_selection",
                    .Prompt = "Select compartment size.",
                    .InputType = "ui_only",
                    .ValidationProfileKey = Nothing,
                    .NextStepKey = Nothing
                }
            }
        },
        New WorkflowDefinition With {
            .WorkflowKey = "asset-checkout",
            .DisplayName = "Check Out",
            .Mode = "asset_workflow",
            .Steps = New List(Of WorkflowStepDefinition) From {
                New WorkflowStepDefinition With {
                    .StepKey = "credential_scan",
                    .Prompt = "Enter Credential",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "credential_default",
                    .NextStepKey = "device_checkout"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "device_checkout",
                    .Prompt = "Retrieving device.",
                    .InputType = "ui_only",
                    .ValidationProfileKey = Nothing,
                    .NextStepKey = Nothing
                }
            }
        },
        New WorkflowDefinition With {
            .WorkflowKey = "asset-deposit",
            .DisplayName = "Check In",
            .Mode = "asset_workflow",
            .Steps = New List(Of WorkflowStepDefinition) From {
                New WorkflowStepDefinition With {
                    .StepKey = "credential_scan",
                    .Prompt = "Enter Credential",
                    .InputType = "scan_or_keypad",
                    .ValidationProfileKey = "credential_default",
                    .NextStepKey = "asset_scan"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "asset_scan",
                    .Prompt = "Scan Asset Tag",
                    .InputType = "scan_only",
                    .ValidationProfileKey = assetValidationProfileKey,
                    .NextStepKey = "defect_decision"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "defect_decision",
                    .Prompt = "Is this device defective?",
                    .InputType = "ui_only",
                    .ValidationProfileKey = Nothing,
                    .NextStepKey = "compartment_assignment"
                },
                New WorkflowStepDefinition With {
                    .StepKey = "compartment_assignment",
                    .Prompt = "Finding available compartment.",
                    .InputType = "ui_only",
                    .ValidationProfileKey = Nothing,
                    .NextStepKey = Nothing
                }
            }
        }
    }

            Dim profiles As New List(Of ScanValidationProfile) From {
        New ScanValidationProfile With {
            .ProfileKey = "credential_default",
            .AllowKeyboardEntry = True,
            .MinLength = 1,
            .MaxLength = 100,
            .RejectMessage = "Credential is not valid."
        },
        New ScanValidationProfile With {
            .ProfileKey = "workorder_default",
            .AllowKeyboardEntry = True,
            .MinLength = 1,
            .MaxLength = 100,
            .RejectMessage = "Work order is not valid."
        },
        New ScanValidationProfile With {
            .ProfileKey = "asset_default",
            .AllowKeyboardEntry = False,
            .RequirePrefix = "S/N",
            .StripPrefix = True,
            .MinLength = 3,
            .MaxLength = 100,
            .AllowedCharactersPattern = "^[A-Za-z0-9\\-_]+$",
            .RejectMessage = "Asset barcode format is not valid."
        }
    }

            Dim config As New KioskWorkflowConfiguration With {
    .DefaultWorkflowKey = pickupWorkflowKey,
    .HomePickupWorkflowKey = pickupWorkflowKey,
    .HomeDeliveryWorkflowKey = deliveryWorkflowKey,
    .EnabledWorkflows = workflows,
    .ScanValidationProfiles = profiles
}

            Dim defaultWorkflowExists As Boolean =
        config.EnabledWorkflows.Any(
            Function(w) w IsNot Nothing AndAlso
                        String.Equals(If(w.WorkflowKey, "").Trim(),
                                      config.DefaultWorkflowKey,
                                      StringComparison.OrdinalIgnoreCase))

            If Not defaultWorkflowExists Then
                config.DefaultWorkflowKey = "package-retrieve"
            End If

            Dim assetProfileExists As Boolean =
        config.ScanValidationProfiles.Any(
            Function(p) p IsNot Nothing AndAlso
                        String.Equals(If(p.ProfileKey, "").Trim(),
                                      assetValidationProfileKey,
                                      StringComparison.OrdinalIgnoreCase))

            If Not assetProfileExists Then
                Dim assetWorkflow = config.EnabledWorkflows.FirstOrDefault(
            Function(w) w IsNot Nothing AndAlso
                        String.Equals(If(w.WorkflowKey, "").Trim(),
                                      "asset-deposit",
                                      StringComparison.OrdinalIgnoreCase))

                If assetWorkflow IsNot Nothing AndAlso assetWorkflow.Steps IsNot Nothing Then
                    Dim assetStep = assetWorkflow.Steps.FirstOrDefault(
                Function(s) s IsNot Nothing AndAlso
                            String.Equals(If(s.StepKey, "").Trim(),
                                          "asset_scan",
                                          StringComparison.OrdinalIgnoreCase))

                    If assetStep IsNot Nothing Then
                        assetStep.ValidationProfileKey = "asset_default"
                    End If
                End If
            End If

            Return config

        End Function
        Private Sub IndexWorkflowConfiguration(config As KioskWorkflowConfiguration)
            _workflowDefinitions.Clear()
            _scanValidationProfiles.Clear()

            If config Is Nothing Then
                Throw New InvalidOperationException("Workflow configuration is not available.")
            End If

            If config.EnabledWorkflows IsNot Nothing Then
                For Each workflow In config.EnabledWorkflows
                    If workflow Is Nothing Then Continue For
                    If String.IsNullOrWhiteSpace(workflow.WorkflowKey) Then Continue For

                    _workflowDefinitions(workflow.WorkflowKey.Trim()) = workflow
                Next
            End If

            If config.ScanValidationProfiles IsNot Nothing Then
                For Each profile In config.ScanValidationProfiles
                    If profile Is Nothing Then Continue For
                    If String.IsNullOrWhiteSpace(profile.ProfileKey) Then Continue For

                    _scanValidationProfiles(profile.ProfileKey.Trim()) = profile
                Next
            End If
        End Sub
#End Region

#Region "Workflow identity helpers"
        Private Function IsUniformDeliveryWorkflow() As Boolean
            Return _activeWorkflow IsNot Nothing AndAlso
           String.Equals(GetActiveWorkflowKey(),
                         "package-deposit",
                         StringComparison.OrdinalIgnoreCase)
        End Function
        Private Function IsAssetManagementWorkflow() As Boolean
            Return _activeWorkflow IsNot Nothing AndAlso
           (String.Equals(GetActiveWorkflowKey(), "asset-deposit", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(GetActiveWorkflowKey(), "asset-checkout", StringComparison.OrdinalIgnoreCase))
        End Function
        Private Function GetActiveWorkflowKey() As String
            If _activeWorkflow Is Nothing Then Return String.Empty
            Return (If(_activeWorkflow.WorkflowKey, String.Empty)).Trim()
        End Function
#End Region

#Region "Windows lifecycle and home workflow selection"
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

            RemoveHandler _barcodeScanService.ScanCompleted, AddressOf OnScanCompleted
            RemoveHandler _barcodeScanService.ScanRejected, AddressOf OnScanRejected
            RemoveHandler _barcodeScanService.Trace, AddressOf OnScannerTrace

            AddHandler _barcodeScanService.ScanCompleted, AddressOf OnScanCompleted
            AddHandler _barcodeScanService.ScanRejected, AddressOf OnScanRejected
            AddHandler _barcodeScanService.Trace, AddressOf OnScannerTrace

            _barcodeScanService.IsEnabled = True
            _barcodeScanService.ResetAll()

            RefreshHomeWorkflowButtons()
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
        Private Sub LockerAccessWindow_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            FocusHidSink()
        End Sub
        Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub
        Private Sub RefreshHomeWorkflowButtons()
            Dim pickupWorkflow = ResolveHomeWorkflow("pickup")
            Dim deliveryWorkflow = ResolveHomeWorkflow("delivery")

            PickupButton.Visibility = If(pickupWorkflow Is Nothing, Visibility.Collapsed, Visibility.Visible)
            DeliverButton.Visibility = If(deliveryWorkflow Is Nothing, Visibility.Collapsed, Visibility.Visible)

            If pickupWorkflow IsNot Nothing Then
                PickupButton.Content = If(String.IsNullOrWhiteSpace(pickupWorkflow.DisplayName), "Pickup", pickupWorkflow.DisplayName)
            End If

            If deliveryWorkflow IsNot Nothing Then
                DeliverButton.Content = If(String.IsNullOrWhiteSpace(deliveryWorkflow.DisplayName), "Deliver", deliveryWorkflow.DisplayName)
            End If
        End Sub
        Private Function ResolveConfiguredWorkflowKey(slotKey As String) As String
            Select Case (If(slotKey, "")).Trim().ToLowerInvariant()
                Case "pickup"
                    Return AppSettings.HomePickupWorkflowKey
                Case "delivery"
                    Return AppSettings.HomeDeliveryWorkflowKey
                Case Else
                    Return String.Empty
            End Select
        End Function
        Private Function ResolveHomeWorkflow(slotKey As String) As WorkflowDefinition
            Dim workflowKey As String = ResolveConfiguredWorkflowKey(slotKey)

            If String.IsNullOrWhiteSpace(workflowKey) Then
                Return Nothing
            End If

            Dim workflow As WorkflowDefinition = Nothing
            If _workflowDefinitions.TryGetValue(workflowKey.Trim(), workflow) Then
                Return workflow
            End If

            Return Nothing
        End Function
#End Region

#Region "Input focus/raw scanner capture"
        Private Sub FocusHidSink()
            Dispatcher.BeginInvoke(
        Sub()
            If HidInputBox Is Nothing Then Return
            HidInputBox.Focus()
            Keyboard.Focus(HidInputBox)
        End Sub,
        System.Windows.Threading.DispatcherPriority.Background)
        End Sub
        Private Sub HidInputBox_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles HidInputBox.PreviewKeyDown
            If e Is Nothing Then Return

            If e.Key = Key.Enter OrElse e.Key = Key.Return Then
                HidInputBox.Clear()
                e.Handled = True
            End If
        End Sub
        Private Sub Window_PreviewTextInput(sender As Object, e As TextCompositionEventArgs) Handles Me.PreviewTextInput
            If e Is Nothing Then Return
            If String.IsNullOrEmpty(e.Text) Then Return
            _barcodeScanService.HandleTextInput(e.Text)
        End Sub
        Private Sub Window_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles Me.PreviewKeyDown
            If e Is Nothing Then Return
            _barcodeScanService.HandleKeyDown(e.Key)
            If e.Key = Key.Enter OrElse e.Key = Key.Return Then
                e.Handled = True
            End If
        End Sub
#End Region

#Region "Scanner event handlers"
        Private Sub RouteScan(raw As String, source As String)
            Dim value = (If(raw, "")).Trim()
            If value.Length = 0 Then Return

            If _state = ScreenState.AwaitAdminCredential Then
                SubmitCredential(value, source)
                Return
            End If

            If _activeStep Is Nothing Then
                Select Case _state
                    Case ScreenState.AwaitCredential
                        SubmitCredential(value, source)
                    Case Else
                        Return
                End Select

                Return
            End If

            Dim stepKey As String = (If(_activeStep.StepKey, "")).Trim().ToLowerInvariant()

            Select Case stepKey
                Case "credential_scan"
                    SubmitCredential(value, source)

                Case "work_order_scan"
                    SubmitWorkOrder(value, source)

                Case "asset_scan"
                    SubmitAssetScan(value, source)

                Case Else
                    Return
            End Select
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
             ScreenState.AwaitWorkOrder,
             ScreenState.AwaitAssetScan

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
#End Region

#Region "Workflow state navigation"
        Private Sub ResetToAwaitWorkflowChoice()

            AuditTrace($"RESET TO HOME state={_state} epoch={_uiEpoch} time={DateTime.Now:HH:mm:ss.fff}",
       reasonCode:="Trace:ResetToAwaitWorkflowChoice")
            TraceToFile("RESET_TO_HOME")

            BumpUiEpoch()

            ' 🔥 CRITICAL FIX
            SetUiEnabled(True)

            _state = ScreenState.AwaitWorkflowChoice
            _activeWorkflow = Nothing
            _activeStep = Nothing

            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _authorizedWorkOrders.Clear()
            _sessionToken = ""

            SizeSelectionPanel.Visibility = Visibility.Collapsed
            SizeTilesItems.ItemsSource = Nothing
            _selectedSizeCode = Nothing

            _activeAssetTag = Nothing
            _selectedCompartmentNumber = Nothing
            _compartmentTiles.Clear()

            RefreshHomeWorkflowButtons()

            PickupButton.IsEnabled = True
            DeliverButton.IsEnabled = True

            KeypadControl.Reset()

            ShowPrompt("Select a workflow")
            FocusHidSink()

        End Sub
        Private Sub PromptForCredential()
            BumpUiEpoch()

            _state = ScreenState.AwaitCredential
            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _activeAssetTag = Nothing
            _authorizedWorkOrders.Clear()

            PickupButton.IsEnabled = False
            DeliverButton.IsEnabled = False

            KeypadControl.Reset()

            If _activeStep IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_activeStep.Prompt) Then
                ShowPrompt(_activeStep.Prompt)
            Else
                ShowPrompt("Enter Credential")
            End If

            FocusHidSink()
        End Sub
        Private Sub SetUiEnabled(enabled As Boolean)
            KeypadControl.IsEnabled = enabled

            If enabled Then
                If _state = ScreenState.AwaitWorkflowChoice Then
                    PickupButton.IsEnabled = True
                    DeliverButton.IsEnabled = True
                Else
                    PickupButton.IsEnabled = False
                    DeliverButton.IsEnabled = False
                End If
            Else
                PickupButton.IsEnabled = False
                DeliverButton.IsEnabled = False
            End If
        End Sub
        Private Sub ShowPrompt(text As String)
            If text = "Select Pickup or Delivery" OrElse text = "Enter Admin Credential" Then
                TraceToFile("SHOWPROMPT: " & text)
            End If

            UserPromptText.Text = text
            SafeFadeIn()
        End Sub
        Private Sub SafeFadeIn()
            Try : fadeIn.Begin() : Catch : End Try
        End Sub
        Private Sub BumpUiEpoch()
            System.Threading.Interlocked.Increment(_uiEpoch)
        End Sub
        Private Sub ActivateWorkflow(workflow As WorkflowDefinition)
            If workflow Is Nothing Then
                Throw New InvalidOperationException("Workflow selection failed because the workflow definition was not found.")
            End If

            BumpUiEpoch()

            _activeWorkflow = workflow
            _activeStep = Nothing

            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _activeAssetTag = Nothing
            _authorizedWorkOrders.Clear()
            _sessionToken = ""

            Dim firstStep As WorkflowStepDefinition = GetFirstStep(workflow)
            If firstStep Is Nothing Then
                Throw New InvalidOperationException($"Workflow '{workflow.WorkflowKey}' does not define any steps.")
            End If

            SetActiveStep(firstStep.StepKey)
        End Sub
        Private Function GetFirstStep(workflow As WorkflowDefinition) As WorkflowStepDefinition
            If workflow Is Nothing OrElse workflow.Steps Is Nothing Then
                Return Nothing
            End If

            Return workflow.Steps.FirstOrDefault(Function(s) s IsNot Nothing)
        End Function
        Private Function GetStep(workflow As WorkflowDefinition, stepKey As String) As WorkflowStepDefinition

            If workflow Is Nothing OrElse workflow.Steps Is Nothing Then
                Return Nothing
            End If

            Dim key As String = (If(stepKey, "")).Trim()
            If key.Length = 0 Then Return Nothing

            Return workflow.Steps.FirstOrDefault(
        Function(s) s IsNot Nothing AndAlso
                    String.Equals((If(s.StepKey, "")).Trim(),
                                  key,
                                  StringComparison.OrdinalIgnoreCase))

        End Function
        Private Sub SetActiveStep(stepKey As String)
            If _activeWorkflow Is Nothing Then
                Throw New InvalidOperationException("No active workflow is selected.")
            End If

            Dim stepDef = GetStep(_activeWorkflow, stepKey)
            If stepDef Is Nothing Then
                Throw New InvalidOperationException(
            $"Workflow '{_activeWorkflow.WorkflowKey}' does not contain step '{stepKey}'.")
            End If

            _activeStep = stepDef

            Dim normalizedStepKey As String = (If(stepDef.StepKey, "")).Trim().ToLowerInvariant()

            Select Case normalizedStepKey
                Case "credential_scan"
                    PromptForCredential()

                Case "work_order_scan"
                    _state = ScreenState.AwaitWorkOrder
                    KeypadControl.Reset()
                    SetUiEnabled(True)
                    ShowPrompt(If(String.IsNullOrWhiteSpace(stepDef.Prompt), "Scan Work Order", stepDef.Prompt))
                    FocusHidSink()

                Case "asset_scan"
                    PromptForAssetScan()

                Case "defect_decision"
                    PromptForDefectDecision()

                Case "size_selection"
                    _state = ScreenState.AwaitLockerSize
                    ShowLockerSizeSelection(If(String.IsNullOrWhiteSpace(stepDef.Prompt), "Select compartment size.", stepDef.Prompt))

                Case "compartment_assignment"
                    ProcessAssetDepositAssignmentAsync()

                Case "device_checkout"
                    BeginAssetCheckoutAsync()

                Case Else
                    Throw New InvalidOperationException($"Unsupported workflow step '{stepDef.StepKey}'.")
            End Select
        End Sub
        Private Sub AdvanceToNextStep()
            If _activeStep Is Nothing Then
                Throw New InvalidOperationException("Cannot advance because there is no active workflow step.")
            End If

            Dim nextKey As String = (If(_activeStep.NextStepKey, "")).Trim()

            If nextKey.Length = 0 Then
                Return
            End If

            SetActiveStep(nextKey)
        End Sub
#End Region

#Region "Home screen workflow selection"
        Private Sub PickupButton_Click(sender As Object, e As RoutedEventArgs) Handles PickupButton.Click
            If _state <> ScreenState.AwaitWorkflowChoice Then Return

            Dim workflow = ResolveHomeWorkflow("pickup")
            If workflow Is Nothing Then
                ShowPrompt("No pickup workflow is configured.")
                Return
            End If

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.User,
                .ActorId = "User:Unknown",
                .AffectedComponent = "LockerAccessWindow",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = Guid.NewGuid().ToString("N"),
                .ReasonCode = $"WorkflowSelected:{workflow.WorkflowKey}"
            })

            ActivateWorkflow(workflow)
        End Sub
        Private Sub DeliverButton_Click(sender As Object, e As RoutedEventArgs) Handles DeliverButton.Click
            If _state <> ScreenState.AwaitWorkflowChoice Then Return

            Dim workflow = ResolveHomeWorkflow("delivery")
            If workflow Is Nothing Then
                ShowPrompt("No delivery workflow is configured.")
                Return
            End If

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.User,
                .ActorId = "User:Unknown",
                .AffectedComponent = "LockerAccessWindow",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = Guid.NewGuid().ToString("N"),
                .ReasonCode = $"WorkflowSelected:{workflow.WorkflowKey}"
            })

            ActivateWorkflow(workflow)
        End Sub
        Private Sub AdminLogo_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs)
            e.Handled = True

            TraceToFile("ADMIN_LOGO_CLICK")

            AuditTrace($"ADMIN LOGO CLICK state={_state} epoch={_uiEpoch} time={DateTime.Now:HH:mm:ss.fff}",
       reasonCode:="Trace:AdminLogoClick")

            If _state = ScreenState.ValidatingCredential Then Return
            If _state = ScreenState.AwaitAdminCredential Then Return

            BumpUiEpoch()


            _activeWorkflow = Nothing
            _activeStep = Nothing

            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _activeAssetTag = Nothing
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
#End Region

#Region "Authentication and step progression"
        Private Sub HandleKeypadSubmit(sender As Object, value As String)
            RouteScan(value, "KEYPAD")
        End Sub
        Private Async Sub SubmitCredential(rawCredential As String, source As String)

            Dim myEpoch As Integer = _uiEpoch

            Dim credential As String = (If(rawCredential, "")).Trim()
            If credential.Length = 0 Then Return

            Dim nowUtc As DateTime = DateTime.UtcNow
            If (nowUtc - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = nowUtc

            If _state = ScreenState.ValidatingCredential Then Return

            Dim isAdminFlow As Boolean = (_state = ScreenState.AwaitAdminCredential)

            If _state <> ScreenState.AwaitCredential AndAlso Not isAdminFlow Then Return

            If Not isAdminFlow AndAlso _activeWorkflow Is Nothing Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            Dim purpose As AuthPurpose =
        If(isAdminFlow, AuthPurpose.AdminAccess, GetPurposeForWorkflow(_activeWorkflow))

            Dim actionId As String = Guid.NewGuid().ToString("N")

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

                _state = If(isAdminFlow, ScreenState.AwaitAdminCredential, ScreenState.AwaitCredential)
                ShowPrompt("System unavailable (" & ex.GetType().Name & ")")
                Return

            Finally
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

            _sessionToken = (If(result.SessionToken, "")).Trim()
            _authResult = result
            _authorizedWorkOrders = If(result.WorkOrders, New List(Of WorkOrderAuthItem)())
            _activeWorkOrder = Nothing

            AuditAuthSucceeded(actionId, purpose, result)

            If purpose = AuthPurpose.AdminAccess Then
                Await ShowAdminPanelAsync(result)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If
                Return
            End If

            Await ContinueWorkflowAfterAuthAsync(result)
        End Sub
        Private Async Function ValidateCredentialWithServerAsync(
    scanValue As String,
    purpose As AuthPurpose,
    source As String
) As Task(Of AuthResult)

            Return Await _backend.AuthorizeAsync(scanValue, purpose, source, CancellationToken.None)
        End Function
        Private Function GetPurposeForWorkflow(workflow As WorkflowDefinition) As AuthPurpose
            If workflow Is Nothing Then
                Throw New InvalidOperationException("No active workflow is selected.")
            End If

            Dim mode As String = (If(workflow.Mode, "")).Trim().ToLowerInvariant()

            Select Case mode
                Case "package_workflow"
                    Return AuthPurpose.PickupAccess

                Case "asset_workflow"
                    Return AuthPurpose.DeliveryCourierAuth

                Case "day_use"
                    Return AuthPurpose.DayUseStart

                Case Else
                    Throw New InvalidOperationException($"Unknown workflow mode '{workflow.Mode}'.")
            End Select
        End Function
        Private Async Function ContinueWorkflowAfterAuthAsync(result As AuthResult) As Task
            If _activeWorkflow Is Nothing Then
                Throw New InvalidOperationException("No active workflow is selected.")
            End If

            If _activeStep Is Nothing Then
                Throw New InvalidOperationException("No active workflow step is selected.")
            End If

            Dim currentStepKey As String = (If(_activeStep.StepKey, "")).Trim().ToLowerInvariant()

            If currentStepKey <> "credential_scan" Then
                Throw New InvalidOperationException(
            $"ContinueWorkflowAfterAuthAsync was called while current step was '{_activeStep.StepKey}' instead of 'credential_scan'.")
            End If

            If String.Equals((If(_activeWorkflow.Mode, "")).Trim(), "uniform_delivery", StringComparison.OrdinalIgnoreCase) OrElse
       String.Equals((If(_activeWorkflow.Mode, "")).Trim(), "asset_management", StringComparison.OrdinalIgnoreCase) Then
                _courierAuth = result
            End If

            AdvanceToNextStep()
            Await Task.CompletedTask
        End Function
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
#End Region

#Region "Pickup workflow"
        Private Async Sub SubmitWorkOrder(rawWorkOrder As String, source As String)

            Dim myEpoch As Integer = _uiEpoch

            Dim wo As String = (If(rawWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim now = DateTime.UtcNow
            If (now - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = now

            If _state = ScreenState.ValidatingCredential Then Return
            If _activeWorkflow Is Nothing Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            ShowPrompt($"Processing work order {wo}…")

            Try
                If String.Equals(GetActiveWorkflowKey(), "package-retrieve", StringComparison.OrdinalIgnoreCase) Then
                    Await ProcessPickupWorkOrderAsync(wo, source, actionId, myEpoch)
                    Return
                End If

                If String.Equals(GetActiveWorkflowKey(), "package-deposit", StringComparison.OrdinalIgnoreCase) Then
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
                .ReasonCode = $"PackageWorkOrderCaptured;WO={wo};Source={source};Workflow={GetActiveWorkflowKey()}"
            })

                    If myEpoch <> _uiEpoch Then Return

                    AdvanceToNextStep()
                    Return
                End If

                If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()

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
        Private Function FindAuthorizedWorkOrder(scannedWorkOrder As String) As WorkOrderAuthItem
            Dim wo = (If(scannedWorkOrder, "")).Trim()
            If wo.Length = 0 Then Return Nothing

            If _authorizedWorkOrders Is Nothing OrElse _authorizedWorkOrders.Count = 0 Then
                Return Nothing
            End If

            Dim desiredTxn As String =
        If(IsUniformDeliveryWorkflow(), "Delivery", "Pickup")

            Dim matches = _authorizedWorkOrders.
        Where(Function(x) x IsNot Nothing AndAlso
                          wo.Equals((If(x.WorkOrderNumber, "")).Trim(),
                                    StringComparison.OrdinalIgnoreCase) AndAlso
                          (String.IsNullOrWhiteSpace(x.TransactionType) OrElse
                           desiredTxn.Equals(x.TransactionType.Trim(),
                                             StringComparison.OrdinalIgnoreCase))).
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
        Private Async Function ProcessPickupWorkOrderAsync(workOrderNumber As String,
                                                   source As String,
                                                   actionId As String,
                                                   myEpoch As Integer) As Task

            If String.IsNullOrWhiteSpace(workOrderNumber) Then Return

            Dim wo = workOrderNumber.Trim()

            ' Validate against authorized list (if present)
            Dim authorized = FindAuthorizedWorkOrder(wo)

            If authorized Is Nothing Then
                If myEpoch <> _uiEpoch Then Return

                ShowPrompt($"Work order {wo} not recognized.")
                _state = ScreenState.AwaitWorkOrder
                Return
            End If

            _activeWorkOrder = authorized

            ' Get lockers assigned to this work order (DB = source of truth)
            Dim lockerNumbers = GetReservedLockerNumbersForWorkOrder(wo)

            If lockerNumbers Is Nothing OrElse lockerNumbers.Count = 0 Then
                If myEpoch <> _uiEpoch Then Return

                ShowPrompt($"No compartments found for work order {wo}.")
                _state = ScreenState.AwaitWorkOrder
                Return
            End If

            ShowPrompt($"Opening {lockerNumbers.Count} compartment(s)...")

            ' Open all lockers for this work order
            For Each lockerNumber In lockerNumbers

                If myEpoch <> _uiEpoch Then Return

                Dim opened = TryOpenLockerWithAudit(actionId, wo, lockerNumber)

                If Not opened Then
                    ' Continue trying others, but log failure
                    Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.LockerOpenAttempt,
                .ActorType = Audit.ActorType.User,
                .ActorId = CurrentActorId(ActorType.User),
                .AffectedComponent = "LockerAccessWindow",
                .Outcome = Audit.AuditOutcome.Failure,
                .CorrelationId = actionId,
                .ReasonCode = $"PickupOpenFailed;WO={wo};Locker={lockerNumber}"
            })
                End If

                Await Task.Delay(200) ' slight spacing between opens
            Next

            If myEpoch <> _uiEpoch Then Return

            ShowPrompt("Please remove items and close all compartments.")

            ' Optionally wait for closure (you already have helper)
            For Each lockerNumber In lockerNumbers
                Await WaitForLockerClosedAsync(lockerNumber, 120000, myEpoch)
                If myEpoch <> _uiEpoch Then Return
            Next

            ' Mark as complete in DB
            CompletePickupForLockers(wo, lockerNumbers)

            If myEpoch <> _uiEpoch Then Return

            ShowPrompt("Pickup complete.")

            Await Task.Delay(1500)

            If myEpoch = _uiEpoch Then
                ResetToAwaitWorkflowChoice()
            End If

        End Function

#End Region

#Region "Shared delivery continuation UI"
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
            _activeAssetTag = Nothing
            _selectedCompartmentNumber = Nothing
            _isDefectiveReturn = False
            _selectedDefectType = Nothing

            If String.Equals(GetActiveWorkflowKey(), "asset-deposit", StringComparison.OrdinalIgnoreCase) Then
                SetActiveStep("asset_scan")
            ElseIf String.Equals(GetActiveWorkflowKey(), "package-deposit", StringComparison.OrdinalIgnoreCase) Then
                SetActiveStep("work_order_scan")
            Else
                ResetToAwaitWorkflowChoice()
                Return
            End If

            KeypadControl.Reset()
            SetUiEnabled(True)
            FocusHidSink()
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
#End Region

#Region "Checkout workflow"
        Private Sub CompleteCheckoutForLocker(deviceType As String, lockerNumber As String)

            Dim requestedType = (If(deviceType, "")).Trim()
            Dim ln = (If(lockerNumber, "")).Trim()

            If requestedType.Length = 0 OrElse ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Return
                End If

                locker.Status.OccupancyState = OccupancyState.Vacant
                locker.Status.PackagePresent = False
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "CheckoutCompleted"
                locker.Status.LastActorId = If(_authResult IsNot Nothing, _authResult.UserId, Nothing)

                locker.Status.LastWorkOrderNumber = Nothing
                locker.Status.ReservedWorkOrderNumber = Nothing
                locker.Status.ReservedCorrelationId = Nothing
                locker.Status.ReservedUntilUtc = Nothing

                locker.Status.CurrentDeviceType = Nothing
                locker.Status.CurrentAssetTag = Nothing
                locker.Status.IsDefectiveHold = False
                locker.Status.DefectType = Nothing

                db.SaveChanges()

            End Using

        End Sub
        Private Async Function ProcessCheckoutAsync(selectedDeviceType As String,
                                            actionId As String,
                                            myEpoch As Integer) As Task

            Dim requestedType As String = (If(selectedDeviceType, "")).Trim()
            If requestedType.Length = 0 Then
                ShowPrompt("No device type was selected.")
                Return
            End If

            Dim lockerNumber As String = _assigner.SelectNextOccupiedLockerNumberByDeviceType(requestedType)

            If String.IsNullOrWhiteSpace(lockerNumber) Then
                ShowPrompt($"No available {requestedType} device was found.")
                Return
            End If

            ShowPrompt($"Opening locker {lockerNumber}...")

            Dim opened As Boolean = TryOpenLockerWithAudit(actionId, requestedType, lockerNumber)

            If Not opened Then
                ShowPrompt($"Unable to open locker {lockerNumber}. Please contact attendant.")
                Return
            End If

            ShowPrompt($"Locker {lockerNumber} opened. Remove device and close the door.")

            Dim closedOk = Await WaitForLockerClosedAsync(
        lockerNumber:=lockerNumber,
        timeoutMs:=120000,
        myEpoch:=myEpoch)

            If myEpoch <> _uiEpoch Then Return

            If Not closedOk Then
                ShowPrompt($"Please close locker {lockerNumber} to continue.")
                Return
            End If

            CompleteCheckoutForLocker(requestedType, lockerNumber)

            ShowPrompt($"{requestedType} checked out successfully.")

            Await Task.Delay(1500)

            If myEpoch = _uiEpoch Then
                ResetToAwaitWorkflowChoice()
            End If

        End Function

#End Region

#Region "Return / Asset deposit workflow"
        Private Async Sub BeginAssetCheckoutAsync()

            Dim myEpoch As Integer = _uiEpoch
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim selectedDeviceType As String = ResolveRequestedCheckoutDeviceType()

            If String.IsNullOrWhiteSpace(selectedDeviceType) Then
                ShowPrompt("No authorized device type was found for this user.")
                Await Task.Delay(1500)
                If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                Return
            End If

            Await ProcessCheckoutAsync(selectedDeviceType, actionId, myEpoch)

        End Sub
        Private Sub PromptForAssetScan()
            BumpUiEpoch()

            _state = ScreenState.AwaitAssetScan
            _activeAssetTag = Nothing
            _selectedCompartmentNumber = Nothing
            _isDefectiveReturn = False
            _selectedDefectType = Nothing

            If _activeWorkflow IsNot Nothing Then
                Dim assetStep = GetStep(_activeWorkflow, "asset_scan")
                If assetStep IsNot Nothing Then
                    _activeStep = assetStep
                End If
            End If

            SizeSelectionPanel.Visibility = Visibility.Collapsed
            KeypadControl.Reset()
            SetUiEnabled(True)

            ShowPrompt(If(_activeStep?.Prompt, "Scan Asset Tag"))
            FocusHidSink()
        End Sub
        Private Function NormalizeAssetTag(raw As String,
                                   ByRef normalized As String,
                                   ByRef errorMessage As String) As Boolean

            normalized = Nothing
            errorMessage = Nothing

            Dim value As String = If(raw, String.Empty).Trim()
            If value.Length = 0 Then
                errorMessage = "Asset barcode is empty."
                Return False
            End If

            Dim profile As ScanValidationProfile = Nothing
            Dim profileKey As String = If(_activeStep?.ValidationProfileKey, String.Empty).Trim()

            If profileKey.Length > 0 Then
                _scanValidationProfiles.TryGetValue(profileKey, profile)
            End If

            If profile IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(profile.RequirePrefix) Then
                If Not value.StartsWith(profile.RequirePrefix, StringComparison.OrdinalIgnoreCase) Then
                    errorMessage = If(profile.RejectMessage, $"Asset barcode must begin with {profile.RequirePrefix}.")
                    Return False
                End If

                If profile.StripPrefix Then
                    value = value.Substring(profile.RequirePrefix.Length).Trim()
                End If
            End If

            If profile IsNot Nothing AndAlso profile.MinLength.HasValue AndAlso value.Length < profile.MinLength.Value Then
                errorMessage = If(profile.RejectMessage, "Asset barcode is too short.")
                Return False
            End If

            If profile IsNot Nothing AndAlso profile.MaxLength.HasValue AndAlso value.Length > profile.MaxLength.Value Then
                errorMessage = If(profile.RejectMessage, "Asset barcode is too long.")
                Return False
            End If

            If profile IsNot Nothing AndAlso
       Not String.IsNullOrWhiteSpace(profile.AllowedCharactersPattern) AndAlso
       Not System.Text.RegularExpressions.Regex.IsMatch(value, profile.AllowedCharactersPattern) Then

                errorMessage = If(profile.RejectMessage, "Asset barcode contains invalid characters.")
                Return False
            End If

            normalized = value.ToUpperInvariant()
            Return True
        End Function
        Private Async Sub SubmitAssetScan(rawAssetScan As String, source As String)

            Dim myEpoch As Integer = _uiEpoch
            Dim assetRaw As String = (If(rawAssetScan, "")).Trim()
            If assetRaw.Length = 0 Then Return

            Dim nowUtc As DateTime = DateTime.UtcNow
            If (nowUtc - _lastSubmitUtc) < _submitDebounce Then Return
            _lastSubmitUtc = nowUtc

            If _state = ScreenState.ValidatingCredential Then Return
            If _state <> ScreenState.AwaitAssetScan Then Return

            Dim normalizedAsset As String = Nothing
            Dim validationError As String = Nothing

            If Not NormalizeAssetTag(assetRaw, normalizedAsset, validationError) Then
                ShowPrompt(validationError)
                FocusHidSink()
                Return
            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            Try
                _activeAssetTag = normalizedAsset

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerAccessWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = Guid.NewGuid().ToString("N"),
            .ReasonCode = $"AssetScanned;AssetTag={normalizedAsset};Source={source};Workflow={GetActiveWorkflowKey()}"
        })

                If myEpoch <> _uiEpoch Then Return

                AdvanceToNextStep()
                Return

            Catch ex As Exception
                If myEpoch <> _uiEpoch Then Return
                ShowPrompt("System unavailable")
                Return

            Finally
                If myEpoch = _uiEpoch Then
                    KeypadControl.Reset()
                    SetUiEnabled(True)
                    FocusHidSink()
                End If
            End Try
        End Sub
        Private Async Sub PromptForDefectDecision()

            _state = ScreenState.AwaitDefectDecision
            SetUiEnabled(False)

            Dim result = Await PromptYesNoAsync(
        message:="Is this device defective?",
        yesText:="Defective",
        noText:="Normal",
        timeoutSeconds:=20,
        myEpoch:=_uiEpoch)

            If result Is Nothing Then
                ' timeout → treat as normal return
                _isDefectiveReturn = False
                AdvanceToNextStep()
                Return
            End If

            If result.Value Then
                _isDefectiveReturn = True
                PromptForDefectType()
            Else
                _isDefectiveReturn = False
                AdvanceToNextStep()
            End If

        End Sub
        Private Async Sub PromptForDefectType()

            _state = ScreenState.AwaitDefectType
            SetUiEnabled(False)

            Dim options As New List(Of String) From {
        "Battery Issue",
        "Screen Damage",
        "Won't Power On",
        "Connectivity Issue",
        "Physical Damage",
        "Missing Parts",
        "Other"
    }

            Dim selected = Await PromptSelectionAsync(
        message:="Select defect type:",
        options:=options,
        timeoutSeconds:=30,
        myEpoch:=_uiEpoch)

            If selected Is Nothing Then
                _selectedDefectType = "Unspecified"
            Else
                _selectedDefectType = selected
            End If

            AdvanceToNextStep()

        End Sub
        Private Async Sub ProcessAssetDepositAssignmentAsync()

            Dim myEpoch As Integer = _uiEpoch
            Dim actionId As String = Guid.NewGuid().ToString("N")

            If String.IsNullOrWhiteSpace(_activeAssetTag) Then
                ShowPrompt("Scan an asset tag first.")
                _state = ScreenState.AwaitAssetScan
                Return
            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            SizeSelectionPanel.Visibility = Visibility.Collapsed

            Dim endSessionAfterDelay As Boolean = False
            Dim errorMessage As String = Nothing

            Try
                ShowPrompt("Finding available compartment...")

                Dim lockerNumber As String = _assigner.SelectNextAvailableLockerNumber("ASSET")

                If String.IsNullOrWhiteSpace(lockerNumber) Then
                    ShowPrompt("No compartments are currently available.")
                    endSessionAfterDelay = True
                Else
                    ShowPrompt($"Reserving compartment {lockerNumber}...")

                    ReserveLockerForDelivery(
                lockerNumber:=lockerNumber,
                workOrderNumber:=_activeAssetTag,
                sizeCode:="ASSET",
                correlationId:=actionId)

                    ShowPrompt($"Opening compartment {lockerNumber}...")

                    Dim opened As Boolean = TryOpenLockerWithAudit(actionId, _activeAssetTag, lockerNumber)

                    If opened Then
                        ShowPrompt($"Compartment {lockerNumber} opened. Place asset inside and close the door.")

                        Dim closedOk = Await WaitForLockerClosedAsync(
                    lockerNumber:=lockerNumber,
                    timeoutMs:=120000,
                    myEpoch:=myEpoch)

                        If myEpoch <> _uiEpoch Then Return

                        If Not closedOk Then
                            ShowPrompt($"Please close compartment {lockerNumber} to continue.")
                            endSessionAfterDelay = True
                        Else
                            CompleteDeliveryForAsset(_activeAssetTag, lockerNumber)

                            Dim deliverAnother = Await PromptDeliverAnotherAsync(
                        timeoutSeconds:=15,
                        myEpoch:=myEpoch)

                            If myEpoch <> _uiEpoch Then Return

                            If deliverAnother.HasValue Then
                                If deliverAnother.Value Then
                                    ContinueDeliverySession()
                                Else
                                    EndDeliverySession()
                                End If
                            Else
                                EndDeliverySession()
                            End If

                            Return
                        End If
                    Else
                        ReleaseLockerReservation(lockerNumber, "AssetOpenFailed")
                        endSessionAfterDelay = True
                    End If
                End If

            Catch ex As Exception
                If myEpoch <> _uiEpoch Then Return
                errorMessage = $"System unavailable: {ex.Message}"
                endSessionAfterDelay = True

            Finally
                If myEpoch = _uiEpoch Then
                    SetUiEnabled(True)
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
        Private Function ResolveDeviceTypeFromAsset(assetTag As String) As String

            Dim tag As String = (If(assetTag, "")).Trim().ToUpperInvariant()

            If tag.Length = 0 Then
                Return "UNKNOWN"
            End If

            ' Example rules — adjust to your actual barcode formats
            If tag.StartsWith("SCN") Then
                Return "SCANNER"
            End If

            If tag.StartsWith("TAB") Then
                Return "TABLET"
            End If

            If tag.StartsWith("PRN") Then
                Return "PRINTER"
            End If

            ' Default
            Return "SCANNER"

        End Function
        Private Function PromptYesNoAsync(message As String,
                                  yesText As String,
                                  noText As String,
                                  timeoutSeconds As Integer,
                                  myEpoch As Integer) As Task(Of Boolean?)

            Dim result As MessageBoxResult =
        MessageBox.Show(message,
                        "Return Condition",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question)

            If myEpoch <> _uiEpoch Then
                Return Task.FromResult(Of Boolean?)(Nothing)
            End If

            If result = MessageBoxResult.Yes Then
                Return Task.FromResult(Of Boolean?)(True)
            End If

            If result = MessageBoxResult.No Then
                Return Task.FromResult(Of Boolean?)(False)
            End If

            Return Task.FromResult(Of Boolean?)(Nothing)

        End Function
        Private Function PromptSelectionAsync(message As String,
                                      options As List(Of String),
                                      timeoutSeconds As Integer,
                                      myEpoch As Integer) As Task(Of String)

            If options Is Nothing OrElse options.Count = 0 Then
                Return Task.FromResult(Of String)(Nothing)
            End If

            Dim prompt As String =
        message & Environment.NewLine & Environment.NewLine &
        String.Join(Environment.NewLine,
                    options.Select(Function(x, i) $"{i + 1}. {x}")) & Environment.NewLine & Environment.NewLine &
        "Enter the number of your selection."

            Dim raw As String = Microsoft.VisualBasic.Interaction.InputBox(
        prompt,
        "Select Defect Type",
        "1")

            If myEpoch <> _uiEpoch Then
                Return Task.FromResult(Of String)(Nothing)
            End If

            If String.IsNullOrWhiteSpace(raw) Then
                Return Task.FromResult(Of String)(Nothing)
            End If

            Dim selectedIndex As Integer
            If Integer.TryParse(raw.Trim(), selectedIndex) Then
                If selectedIndex >= 1 AndAlso selectedIndex <= options.Count Then
                    Return Task.FromResult(options(selectedIndex - 1))
                End If
            End If

            Return Task.FromResult(Of String)(Nothing)

        End Function
        Private Function ResolveRequestedCheckoutDeviceType() As String

            If _authResult Is Nothing OrElse _authResult.Roles Is Nothing Then
                Return Nothing
            End If

            ' If only one device type → auto select
            If _authResult.Roles.Count = 1 Then
                Return _authResult.Roles(0).ToUpperInvariant()
            End If

            ' 🔜 Future: multi-device selection UI
            ' For now just pick first
            Return _authResult.Roles(0).ToUpperInvariant()

        End Function
#End Region

#Region "Shared locker interaction helpers"
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
        Private Sub CompleteDeliveryForAsset(assetTag As String, lockerNumber As String)

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = lockerNumber)

                If locker Is Nothing OrElse locker.Status Is Nothing Then Return

                locker.Status.OccupancyState = OccupancyState.Occupied
                locker.Status.PackagePresent = True
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "AssetDeposited"
                locker.Status.LastActorId = If(_courierAuth IsNot Nothing, _courierAuth.UserId, Nothing)

                ' 🔥 KEY NEW FIELDS
                locker.Status.CurrentAssetTag = assetTag
                locker.Status.CurrentDeviceType = ResolveDeviceTypeFromAsset(assetTag)
                locker.Status.IsDefectiveHold = _isDefectiveReturn
                locker.Status.DefectType = If(_isDefectiveReturn, _selectedDefectType, Nothing)

                db.SaveChanges()

            End Using

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
#End Region

#Region "Backend / Locker actions"
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
#End Region

#Region "Delivery Compartment Size selection"
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

            If Not IsUniformDeliveryWorkflow() Then
                ShowPrompt("Size selection is only used for the uniform delivery workflow.")
                Return False
            End If

            Return True
        End Function
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
        Private Sub HideLockerSizeSelection()
            SizeSelectionPanel.Visibility = Visibility.Collapsed
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

            If _activeWorkflow IsNot Nothing Then
                Dim stepDef = GetStep(_activeWorkflow, "size_selection")
                If stepDef IsNot Nothing Then
                    _activeStep = stepDef
                End If
            End If

            ShowPrompt(promptText)
        End Sub
#End Region

#Region "Audit/Utility/diagnostics"
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
        Private Function NewRequestId() As String
            Return Guid.NewGuid().ToString("D")
        End Function
        Private Function UtcNowIso() As String
            Return DateTime.UtcNow.ToString("o")
        End Function
        Private Function Truncate(s As String, maxLen As Integer) As String
            Dim t = If(s, "")
            If t.Length <= maxLen Then Return t
            Return t.Substring(0, maxLen)
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
        Private Sub RequireBackendConfig()
            ' Respect TestModeEnabled
            AppSettings.RequireBackendConfig()
            If AppSettings.TestModeEnabled Then Return

            ' Window-level extras (only when not test mode)
            If String.IsNullOrWhiteSpace(AppSettings.LocationId) Then
                Throw New InvalidOperationException("LocationId is not configured.")
            End If
        End Sub
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


#End Region

        Private Function ValidateDeliveryWorkOrder() As Boolean

            If _activeWorkOrder Is Nothing OrElse String.IsNullOrWhiteSpace(_activeWorkOrder.WorkOrderNumber) Then
                ShowPrompt("Scan a Work Order first.")
                _state = ScreenState.AwaitWorkOrder
                Return False
            End If

            Return True

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


    End Class
End Namespace



