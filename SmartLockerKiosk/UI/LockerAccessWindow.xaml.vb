Imports System.Collections.ObjectModel
Imports System.ComponentModel
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
        Private _touchSelectionTcs As TaskCompletionSource(Of String) = Nothing
        Private _returnToStartRequested As Boolean = False
        Private _workflowCancellation As CancellationTokenSource = Nothing
        Private _inactivityTimer As Threading.DispatcherTimer = Nothing
        Private _lastUserActivityUtc As DateTime = DateTime.UtcNow
        Private _lockerDoorCycleActive As Boolean = False
        Private _lastDoorCycleReturnRequestUtc As DateTime = DateTime.MinValue

        Private ReadOnly _barcodeScanService As BarcodeScanService

        Private _activeAssetTag As String = Nothing
        Private _activeDeviceType As String = Nothing
        Private _activeSizeCode As String = Nothing
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
        Private _lastCredentialKey As String = ""

#Region "Initialization and Configuration Loading"
        Public Sub New(lockerController As LockerControllerService)
            InitializeComponent()
            fadeIn = CType(FindResource("FadeInPrompt"), Storyboard)



            ApplyTheme(AppSettings.SelectedStyle)

            _lockerController = lockerController

            If AppSettings.UseBackendBypass Then
                _backend = New BypassOperationsBackendService()
            Else
                _backend = New OperationsBackendService(BackendHttpFactory.CreateHttpClient())
            End If

            _barcodeScanService = New BarcodeScanService()

            _workflowConfig = LoadWorkflowConfiguration()

            TraceLogger.Log(
    $"WORKFLOW CONFIG ACTIVE: " &
    $"ClientCode={AppSettings.ClientCode}; " &
    $"WorkflowFamily={AppSettings.WorkflowFamily}; " &
    $"DefaultWorkflow={_workflowConfig.DefaultWorkflowKey}; " &
    $"PickupWorkflow={_workflowConfig.HomePickupWorkflowKey}; " &
    $"StageWorkflow={_workflowConfig.HomeDeliveryWorkflowKey}")

            For Each wf In _workflowConfig.EnabledWorkflows

                If wf Is Nothing Then Continue For

                TraceLogger.Log(
        $"WORKFLOW REGISTERED: " &
        $"Key={wf.WorkflowKey}; " &
        $"Display={wf.DisplayName}; " &
$"HomeButtonLabel={wf.HomeButtonLabel}; " &
$"Mode={wf.Mode}; " &
$"Action={wf.WorkflowAction}")

            Next

            IndexWorkflowConfiguration(_workflowConfig)

            ConfigureBarcodeValidation()
        End Sub
        Private Sub ApplyTheme(themeName As String)

            RemoveThemeDictionaries(Application.Current.Resources.MergedDictionaries)
            RemoveThemeDictionaries(Me.Resources.MergedDictionaries)

            Dim themeUri As Uri

            Select Case If(themeName, "").Trim().ToUpperInvariant()
                Case "RYDER"
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/RyderTheme.xaml", UriKind.Relative)

                Case "SHAW", "SHAWAFB", "SHAW_AFB"
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/ShawAFBTheme.xaml", UriKind.Relative)

                Case "TSA"
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/TsaTheme.xaml", UriKind.Relative)

                Case Else
                    themeUri = New Uri("/SmartLockerKiosk;component/Themes/TsaTheme.xaml", UriKind.Relative)
            End Select

            Me.Resources.MergedDictionaries.Add(New ResourceDictionary With {.Source = themeUri})

        End Sub
        Private Sub RemoveThemeDictionaries(merged As Collection(Of ResourceDictionary))

            For i As Integer = merged.Count - 1 To 0 Step -1

                Dim src = merged(i).Source?.ToString()

                If src IsNot Nothing AndAlso
           (src.Contains("TsaTheme.xaml") OrElse
            src.Contains("RyderTheme.xaml") OrElse
            src.Contains("ShawAFBTheme.xaml")) Then

                    merged.RemoveAt(i)

                End If

            Next

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
            Dim configPath As String = ResolveWorkflowConfigPath()

            If Not String.IsNullOrWhiteSpace(configPath) Then

                Try
                    Dim jsonConfig As KioskWorkflowConfiguration =
            LoadWorkflowConfigurationFromJson(configPath)

                    If jsonConfig IsNot Nothing Then
                        TraceLogger.Log($"WORKFLOW CONFIG loaded from JSON: {configPath}")
                        Return NormalizeWorkflowConfiguration(jsonConfig)
                    End If

                    TraceLogger.Log($"WORKFLOW CONFIG JSON not found or empty. Falling back to built-in defaults. Path={configPath}")

                Catch ex As Exception

                    TraceExceptionDeep("WORKFLOW_CONFIG_JSON_LOAD_FAILED", ex)

                    TraceLogger.Log(
            "WORKFLOW CONFIG JSON failed validation/load. " &
            "Falling back to built-in defaults. " &
            $"Path={configPath}; Error={ex.Message}")

                End Try

            End If

            Dim pickupWorkflowKey As String =
        If(AppSettings.HomePickupWorkflowKey, "package-retrieve").Trim()

            Dim stageWorkflowKey As String =
        If(AppSettings.HomeDeliveryWorkflowKey, "package-deposit").Trim()

            Dim assetValidationProfileKey As String =
        If(AppSettings.DefaultAssetValidationProfileKey, "asset_default").Trim()

            If String.IsNullOrWhiteSpace(pickupWorkflowKey) Then
                pickupWorkflowKey = "package-retrieve"
            End If

            If String.IsNullOrWhiteSpace(stageWorkflowKey) Then
                stageWorkflowKey = "package-deposit"
            End If

            If String.IsNullOrWhiteSpace(assetValidationProfileKey) Then
                assetValidationProfileKey = "asset_default"
            End If

            Dim workflows As New List(Of WorkflowDefinition) From {
        New WorkflowDefinition With {
            .WorkflowKey = "package-retrieve",
            .DisplayName = "Retrieve",
            .Mode = "package_workflow",
            .WorkflowAction = "pickup",
            .Options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"ReferenceLabel", "Work Order"},
                {"RequiresReferenceAuthorization", "true"},
                {"UseSizeSelection", "false"}
            },
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
            .WorkflowAction = "stage",
            .Options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"ReferenceLabel", "Work Order"},
                {"RequiresReferenceAuthorization", "false"},
                {"UseSizeSelection", "true"}
            },
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
    .HomeButtonLabel = "Check Out",
    .Mode = "asset_workflow",
            .WorkflowAction = "pickup",
            .Options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"ReferenceLabel", "Device Type"}
            },
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
    .HomeButtonLabel = "Check In",
    .Mode = "asset_workflow",
            .WorkflowAction = "stage",
            .Options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"ReferenceLabel", "Asset Tag"}
            },
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
            .RejectMessage = "Reference is not valid."
        },
        New ScanValidationProfile With {
            .ProfileKey = "asset_default",
            .AllowKeyboardEntry = False,
            .RequirePrefix = "S",
            .StripPrefix = False,
            .MinLength = 3,
            .MaxLength = 100,
            .AllowedCharactersPattern = "^[A-Za-z0-9\-_]+$",
            .RejectMessage = "Scan serial number (S/N), not MAC label."
        }
    }

            Dim config As New KioskWorkflowConfiguration With {
        .DefaultWorkflowKey = pickupWorkflowKey,
        .HomePickupWorkflowKey = pickupWorkflowKey,
        .HomeDeliveryWorkflowKey = stageWorkflowKey,
        .EnabledWorkflows = workflows,
        .ScanValidationProfiles = profiles
    }

            EnsureConfiguredWorkflowExists(config, pickupWorkflowKey, "package-retrieve")
            EnsureConfiguredWorkflowExists(config, stageWorkflowKey, "package-deposit")
            EnsureAssetValidationProfileExists(config, assetValidationProfileKey)

            Return NormalizeWorkflowConfiguration(config)

        End Function
        Private Function LoadWorkflowConfigurationFromJson(configPath As String) As KioskWorkflowConfiguration

            If String.IsNullOrWhiteSpace(configPath) Then
                TraceLogger.Log("WORKFLOW CONFIG JSON path is blank.")
                Return Nothing
            End If

            If Not IO.File.Exists(configPath) Then
                TraceLogger.Log($"WORKFLOW CONFIG JSON file not found: {configPath}")
                Return Nothing
            End If

            Dim json As String = IO.File.ReadAllText(configPath)

            If String.IsNullOrWhiteSpace(json) Then
                TraceLogger.Log($"WORKFLOW CONFIG JSON file is empty: {configPath}")
                Return Nothing
            End If

            Dim options As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .ReadCommentHandling = JsonCommentHandling.Skip,
        .AllowTrailingCommas = True
    }

            Return JsonSerializer.Deserialize(Of KioskWorkflowConfiguration)(json, options)

        End Function
        Private Function NormalizeWorkflowConfiguration(config As KioskWorkflowConfiguration) As KioskWorkflowConfiguration

            If config Is Nothing Then
                Throw New InvalidOperationException("Workflow configuration could not be loaded.")
            End If

            If config.EnabledWorkflows Is Nothing Then
                config.EnabledWorkflows = New List(Of WorkflowDefinition)()
            End If

            If config.ScanValidationProfiles Is Nothing Then
                config.ScanValidationProfiles = New List(Of ScanValidationProfile)()
            End If

            For Each workflow In config.EnabledWorkflows

                If workflow Is Nothing Then Continue For

                workflow.WorkflowKey = If(workflow.WorkflowKey, "").Trim()
                workflow.DisplayName = If(workflow.DisplayName, "").Trim()
                workflow.HomeButtonLabel = If(workflow.HomeButtonLabel, "").Trim()
                workflow.Mode = If(workflow.Mode, "").Trim()
                workflow.WorkflowAction = If(workflow.WorkflowAction, "").Trim().ToLowerInvariant()

                If workflow.Options Is Nothing Then
                    workflow.Options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                Else
                    workflow.Options =
                New Dictionary(Of String, String)(workflow.Options, StringComparer.OrdinalIgnoreCase)
                End If

                If workflow.Steps Is Nothing Then
                    workflow.Steps = New List(Of WorkflowStepDefinition)()
                End If

                For Each stepDef In workflow.Steps
                    If stepDef Is Nothing Then Continue For

                    stepDef.StepKey = If(stepDef.StepKey, "").Trim()
                    stepDef.Prompt = If(stepDef.Prompt, "").Trim()
                    stepDef.InputType = If(stepDef.InputType, "").Trim().ToLowerInvariant()
                    stepDef.ValidationProfileKey = If(stepDef.ValidationProfileKey, "").Trim()
                    stepDef.NextStepKey = If(stepDef.NextStepKey, "").Trim()
                Next

            Next

            For Each profile In config.ScanValidationProfiles
                If profile Is Nothing Then Continue For

                profile.ProfileKey = If(profile.ProfileKey, "").Trim()
                profile.RequirePrefix = If(profile.RequirePrefix, "").Trim()
                profile.RejectMessage = If(profile.RejectMessage, "").Trim()
                profile.AllowedCharactersPattern = If(profile.AllowedCharactersPattern, "").Trim()
            Next

            config.DefaultWorkflowKey = If(config.DefaultWorkflowKey, "").Trim()
            config.HomePickupWorkflowKey = If(config.HomePickupWorkflowKey, "").Trim()
            config.HomeDeliveryWorkflowKey = If(config.HomeDeliveryWorkflowKey, "").Trim()

            ValidateWorkflowConfiguration(config)

            Return config

        End Function
        Private Sub ValidateWorkflowConfiguration(config As KioskWorkflowConfiguration)
            WorkflowConfigurationValidator.Validate(config)
        End Sub
        Private Function ResolveWorkflowConfigPath() As String

            Dim configuredPath As String =
        If(AppSettings.WorkflowConfigPath, "").Trim()

            If Not String.IsNullOrWhiteSpace(configuredPath) Then

                If IO.Path.IsPathRooted(configuredPath) Then
                    Return configuredPath
                End If

                Return IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath)

            End If

            Dim clientCode As String =
        If(AppSettings.ClientCode, "").Trim().ToLowerInvariant()

            If String.IsNullOrWhiteSpace(clientCode) Then
                Return ""
            End If

            Return IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Configs",
        $"{clientCode}-workflow.json")

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
        Private Sub EnsureConfiguredWorkflowExists(config As KioskWorkflowConfiguration,
                                           configuredKey As String,
                                           fallbackKey As String)

            If config Is Nothing OrElse config.EnabledWorkflows Is Nothing Then Return

            Dim exists As Boolean =
        config.EnabledWorkflows.Any(
            Function(w) w IsNot Nothing AndAlso
                        String.Equals(If(w.WorkflowKey, "").Trim(),
                                      configuredKey,
                                      StringComparison.OrdinalIgnoreCase))

            If exists Then Return

            If String.Equals(config.HomePickupWorkflowKey, configuredKey, StringComparison.OrdinalIgnoreCase) Then
                config.HomePickupWorkflowKey = fallbackKey
            End If

            If String.Equals(config.HomeDeliveryWorkflowKey, configuredKey, StringComparison.OrdinalIgnoreCase) Then
                config.HomeDeliveryWorkflowKey = fallbackKey
            End If

            If String.Equals(config.DefaultWorkflowKey, configuredKey, StringComparison.OrdinalIgnoreCase) Then
                config.DefaultWorkflowKey = fallbackKey
            End If

        End Sub
        Private Sub EnsureAssetValidationProfileExists(config As KioskWorkflowConfiguration,
                                               assetValidationProfileKey As String)

            If config Is Nothing OrElse config.ScanValidationProfiles Is Nothing Then Return

            Dim profileExists As Boolean =
        config.ScanValidationProfiles.Any(
            Function(p) p IsNot Nothing AndAlso
                        String.Equals(If(p.ProfileKey, "").Trim(),
                                      assetValidationProfileKey,
                                      StringComparison.OrdinalIgnoreCase))

            If profileExists Then Return

            Dim assetWorkflow = config.EnabledWorkflows.FirstOrDefault(
    Function(w) w IsNot Nothing AndAlso
                String.Equals(If(w.Mode, "").Trim(), "asset_workflow", StringComparison.OrdinalIgnoreCase) AndAlso
                String.Equals(If(w.WorkflowAction, "").Trim(), "stage", StringComparison.OrdinalIgnoreCase))

            If assetWorkflow Is Nothing OrElse assetWorkflow.Steps Is Nothing Then Return

            Dim assetStep = assetWorkflow.Steps.FirstOrDefault(
        Function(s) s IsNot Nothing AndAlso
                    String.Equals(If(s.StepKey, "").Trim(),
                                  "asset_scan",
                                  StringComparison.OrdinalIgnoreCase))

            If assetStep IsNot Nothing Then
                assetStep.ValidationProfileKey = "asset_default"
            End If

        End Sub
#End Region

#Region "Workflow identity helpers"
        Private Function IsPackageStageWorkflow() As Boolean

            If _activeWorkflow Is Nothing Then Return False

            Dim mode As String =
        If(_activeWorkflow.Mode, "").Trim().ToLowerInvariant()

            Dim action As String =
        GetWorkflowAction(_activeWorkflow)

            Return mode = "package_workflow" AndAlso action = "stage"

        End Function
        Private Function IsAssetManagementWorkflow() As Boolean

            If _activeWorkflow Is Nothing Then Return False

            Return String.Equals(
        If(_activeWorkflow.Mode, "").Trim(),
        "asset_workflow",
        StringComparison.OrdinalIgnoreCase)

        End Function
        Private Function GetActiveWorkflowKey() As String
            If _activeWorkflow Is Nothing Then Return String.Empty
            Return (If(_activeWorkflow.WorkflowKey, String.Empty)).Trim()
        End Function
#End Region

#Region "Windows lifecycle and home workflow selection"
        Private Async Sub LockerAccessWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

            DatabaseBootstrapper.InitializeDatabase()

            If _loadedOnce Then Return
            _loadedOnce = True

            StartPendingCommitFlusher()
            StartWorkflowInactivityTimer()

            Dim startupRecoveryPrompt As String = Await BuildStartupRecoveryPromptAsync()

            RemoveHandler KeypadControl.PasscodeComplete, AddressOf HandleKeypadSubmit
            AddHandler KeypadControl.PasscodeComplete, AddressOf HandleKeypadSubmit
            KeypadControl.AddHandler(
                Button.ClickEvent,
                New RoutedEventHandler(AddressOf KeypadButton_Click),
                True)

            Try
                fadeIn.Begin()

            Catch ex As Exception
                TraceExceptionDeep("FADE_IN_START_FAILED", ex)
            End Try

            Me.WindowStyle = WindowStyle.None
            Me.ResizeMode = ResizeMode.NoResize
            Me.Topmost = True
            Me.WindowStartupLocation = WindowStartupLocation.CenterScreen
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

            If Not String.IsNullOrWhiteSpace(startupRecoveryPrompt) Then
                ShowPrompt(startupRecoveryPrompt)

                Await Task.Delay(5000)

                ResetToAwaitWorkflowChoice()
            Else
                ResetToAwaitWorkflowChoice()
            End If

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
        Private Async Function BuildStartupRecoveryPromptAsync() As Task(Of String)

            Try
                Dim recovery As New LockerTransactionRecoveryService()

                Dim incomplete = Await recovery.GetIncompleteTransactionsAsync(25)

                If incomplete Is Nothing OrElse incomplete.Count = 0 Then
                    Return ""
                End If

                TraceLogger.Log($"RECOVERY found {incomplete.Count} incomplete locker transaction(s).")

                For Each tx In incomplete
                    TraceLogger.Log(
                $"RECOVERY incomplete tx Id={tx.Id}; " &
                $"Workflow={tx.Workflow}; Action={tx.ActionType}; " &
                $"Locker={tx.LockerNumber}; State={tx.TransactionState}; Ack={tx.AckStatus}; " &
                $"CreatedUtc={tx.CreatedUtc:u}")
                Next

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.IntegrityCheckFailed,
            .ActorType = Audit.ActorType.System,
            .ActorId = "System:RecoveryScanner",
            .AffectedComponent = "LockerTransactionRecoveryService",
            .Outcome = Audit.AuditOutcome.Detected,
            .CorrelationId = Guid.NewGuid().ToString("N"),
            .ReasonCode = $"IncompleteTransactionsDetected;Count={incomplete.Count}"
        })

                Dim firstItems =
                    incomplete.
                    Take(3).
                    Select(Function(tx)
                               Return $"#{tx.Id} {tx.Workflow}/{tx.ActionType} L{tx.LockerNumber} {tx.TransactionState}/{tx.AckStatus}"
                           End Function).
                    ToList()

                Dim summary As String =
                    "Warning: incomplete locker transaction(s) detected." &
                    Environment.NewLine &
                    $"Count: {incomplete.Count}" &
                    Environment.NewLine &
                    String.Join(Environment.NewLine, firstItems)

                If incomplete.Count > firstItems.Count Then
                    summary &= Environment.NewLine & $"...and {incomplete.Count - firstItems.Count} more."
                End If

                summary &=
                    Environment.NewLine &
                    Environment.NewLine &
                    "Open Admin > Transaction Recovery for review."

                Return summary

            Catch ex As Exception

                TraceExceptionDeep("RECOVERY_SCAN_FAILED", ex)

                ' Do not block kiosk startup if recovery scan itself fails.
                Return ""

            End Try

        End Function
        Private Sub LockerAccessWindow_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            FocusHidSink()
        End Sub
        Private Sub CloseButton_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub
        Private Sub RefreshHomeWorkflowButtons()

            Try

                TraceLogger.Log("REFRESH HOME WORKFLOW BUTTONS START")

                Dim pickupWorkflow = ResolveHomeWorkflow("pickup")
                Dim stageWorkflow = ResolveHomeWorkflow("delivery")

                PickupButton.Visibility =
            If(pickupWorkflow Is Nothing,
               Visibility.Collapsed,
               Visibility.Visible)

                DeliverButton.Visibility =
            If(stageWorkflow Is Nothing,
               Visibility.Collapsed,
               Visibility.Visible)

                If pickupWorkflow IsNot Nothing Then

                    Dim pickupLabel As String =
                GetWorkflowButtonLabel(pickupWorkflow, "Check Out")

                    PickupButton.Content = pickupLabel
                    PickupButton.Tag = pickupWorkflow.WorkflowKey

                    TraceLogger.Log(
                "PICKUP BUTTON configured. " &
                "Label='" & pickupLabel & "', " &
                "WorkflowKey='" & pickupWorkflow.WorkflowKey & "', " &
                "WorkflowAction='" & pickupWorkflow.WorkflowAction & "'")

                End If

                If stageWorkflow IsNot Nothing Then

                    Dim stageLabel As String =
                GetWorkflowButtonLabel(stageWorkflow, "Check In")

                    DeliverButton.Content = stageLabel
                    DeliverButton.Tag = stageWorkflow.WorkflowKey

                    TraceLogger.Log(
                "DELIVER BUTTON configured. " &
                "Label='" & stageLabel & "', " &
                "WorkflowKey='" & stageWorkflow.WorkflowKey & "', " &
                "WorkflowAction='" & stageWorkflow.WorkflowAction & "'")

                End If

                TraceLogger.Log("REFRESH HOME WORKFLOW BUTTONS END")

            Catch ex As Exception

                TraceLogger.Log(
            "REFRESH HOME WORKFLOW BUTTONS ERROR: " &
            ex.ToString())

            End Try

        End Sub


        Private Function GetWorkflowButtonLabel(
    workflow As WorkflowDefinition,
    fallbackText As String) As String

            Try

                If workflow Is Nothing Then
                    Return fallbackText
                End If

                If Not String.IsNullOrWhiteSpace(workflow.HomeButtonLabel) Then
                    Return workflow.HomeButtonLabel.Trim()
                End If

                If Not String.IsNullOrWhiteSpace(workflow.DisplayName) Then
                    Return workflow.DisplayName.Trim()
                End If

                Return fallbackText

            Catch ex As Exception

                TraceLogger.Log(
            "GET WORKFLOW BUTTON LABEL ERROR: " &
            ex.ToString())

                Return fallbackText

            End Try

        End Function
        Private Function ResolveConfiguredWorkflowKey(slotKey As String) As String

            If _workflowConfig Is Nothing Then Return String.Empty

            Select Case (If(slotKey, "")).Trim().ToLowerInvariant()

                Case "pickup", "retrieve"
                    Return If(_workflowConfig.HomePickupWorkflowKey, "").Trim()

                Case "delivery", "stage", "deposit"
                    Return If(_workflowConfig.HomeDeliveryWorkflowKey, "").Trim()

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
        Private Function GetCurrentWorkflowReference(fallbackReference As String) As String

            If Not String.IsNullOrWhiteSpace(_activeAssetTag) Then
                Return _activeAssetTag.Trim()
            End If

            If _activeWorkOrder IsNot Nothing AndAlso
       Not String.IsNullOrWhiteSpace(_activeWorkOrder.WorkOrderNumber) Then

                Return _activeWorkOrder.WorkOrderNumber.Trim()
            End If

            Return If(fallbackReference, "").Trim()

        End Function
        Private Function GetWorkflowOption(key As String,
                                   Optional defaultValue As String = "") As String

            If _activeWorkflow Is Nothing Then Return defaultValue
            If String.IsNullOrWhiteSpace(key) Then Return defaultValue

            If _activeWorkflow.Options Is Nothing Then Return defaultValue

            Dim value As String = Nothing

            If _activeWorkflow.Options.TryGetValue(key.Trim(), value) Then
                If Not String.IsNullOrWhiteSpace(value) Then
                    Return value.Trim()
                End If
            End If

            Return defaultValue

        End Function
        Private Function GetWorkflowOptionBoolean(key As String,
                                          Optional defaultValue As Boolean = False) As Boolean

            Dim raw As String = GetWorkflowOption(key, "")

            If String.IsNullOrWhiteSpace(raw) Then Return defaultValue

            Dim parsed As Boolean
            If Boolean.TryParse(raw.Trim(), parsed) Then
                Return parsed
            End If

            Select Case raw.Trim().ToLowerInvariant()
                Case "1", "yes", "y", "true"
                    Return True
                Case "0", "no", "n", "false"
                    Return False
                Case Else
                    Return defaultValue
            End Select

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
            RecordUserActivity()
            _barcodeScanService.HandleTextInput(e.Text)
        End Sub
        Private Sub Window_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles Me.PreviewKeyDown
            If e Is Nothing Then Return
            RecordUserActivity()
            _barcodeScanService.HandleKeyDown(e.Key)
            If e.Key = Key.Enter OrElse e.Key = Key.Return Then
                e.Handled = True
            End If
        End Sub
        Private Sub Window_PreviewMouseDown(sender As Object, e As MouseButtonEventArgs) Handles Me.PreviewMouseDown
            RecordUserActivity()
        End Sub
        Private Sub Window_PreviewTouchDown(sender As Object, e As TouchEventArgs) Handles Me.PreviewTouchDown
            RecordUserActivity()
        End Sub
#End Region

#Region "Scanner event handlers"
        Private Sub RouteScan(raw As String, source As String)
            Dim value = (If(raw, "")).Trim()
            If value.Length = 0 Then Return

            If _state = ScreenState.AwaitAdminCredential Then
                StartWorkflowTask(SubmitCredentialAsync(value, source), "SubmitCredential")
                Return
            End If

            If _activeStep Is Nothing Then
                Select Case _state
                    Case ScreenState.AwaitCredential
                        StartWorkflowTask(SubmitCredentialAsync(value, source), "SubmitCredential")
                    Case Else
                        Return
                End Select

                Return
            End If

            Dim stepKey As String = (If(_activeStep.StepKey, "")).Trim().ToLowerInvariant()

            Select Case stepKey
                Case "credential_scan"
                    StartWorkflowTask(SubmitCredentialAsync(value, source), "SubmitCredential")

                Case "work_order_scan"
                    StartWorkflowTask(SubmitWorkOrderAsync(value, source), "SubmitWorkOrder")

                Case "asset_scan"
                    StartWorkflowTask(SubmitAssetScanAsync(value, source), "SubmitAssetScan")

                Case Else
                    Return
            End Select
        End Sub
        Private Async Sub StartWorkflowTask(task As Task, operationName As String)
            Try
                Await task
            Catch ex As OperationCanceledException
                TraceLogger.Log($"{operationName} cancelled.")
                If _returnToStartRequested Then
                    CompleteDeferredReturnToStartIfRequested()
                End If
            Catch ex As Exception
                TraceExceptionDeep(operationName & "_UNHANDLED", ex)
                ShowPrompt("The workflow could not continue. Returning to start.")
                Dim resetOperation = Dispatcher.BeginInvoke(
                    Async Sub()
                        Await Task.Delay(1500)
                        ResetToAwaitWorkflowChoice()
                    End Sub)
            End Try
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
            _returnToStartRequested = False
            CancelWorkflowRequests()

            If _touchSelectionTcs IsNot Nothing Then
                _touchSelectionTcs.TrySetResult(Nothing)
                _touchSelectionTcs = Nothing
            End If

            If _deliverAnotherTcs IsNot Nothing Then
                _deliverAnotherTcs.TrySetResult(Nothing)
            End If

            HideDeliverAnotherPrompt()

            ' 🔥 CRITICAL FIX
            SetUiEnabled(True)

            _state = ScreenState.AwaitWorkflowChoice
            _activeWorkflow = Nothing
            _activeStep = Nothing
            _lastCredentialKey = ""

            _authResult = Nothing
            _courierAuth = Nothing
            _activeWorkOrder = Nothing
            _authorizedWorkOrders.Clear()
            _sessionToken = ""

            SizeSelectionPanel.Visibility = Visibility.Collapsed
            SizeTilesItems.ItemsSource = Nothing
            _selectedSizeCode = Nothing
            DefectPanel.Visibility = Visibility.Collapsed
            DefectButtonGrid.Children.Clear()
            ReturnToStartButton.Visibility = Visibility.Collapsed

            _activeAssetTag = Nothing
            _selectedCompartmentNumber = Nothing
            _compartmentTiles.Clear()

            RefreshHomeWorkflowButtons()

            PickupButton.IsEnabled = True
            DeliverButton.IsEnabled = True

            KeypadControl.Reset()

            ShowPrompt("What would you like to do?")
            FocusHidSink()

        End Sub
        Private Sub PromptForCredential()
            BumpUiEpoch()
            BeginWorkflowCancellationScope()

            _barcodeScanService.Validator = Nothing

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
            ' Keep CLR available while requests are running. State guards prevent
            ' duplicate submissions from the remaining keypad buttons.
            KeypadControl.IsEnabled = True

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
        Private Sub KeypadButton_Click(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(e.OriginalSource, Button)
            If button Is Nothing Then Return
            RecordUserActivity()

            Dim content As String = If(button.Content, "").ToString().Trim()
            If Not String.Equals(content, "CLR", StringComparison.OrdinalIgnoreCase) Then Return

            RequestReturnToStart()
        End Sub
        Private Sub WorkflowClearButton_Click(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            RequestReturnToStart()
        End Sub
        Private Sub RequestReturnToStart()
            KeypadControl.Reset()
            _barcodeScanService.ResetAll()

            If _state = ScreenState.AwaitWorkflowChoice Then Return

            If _lockerDoorCycleActive AndAlso
               (DateTime.UtcNow - _lastDoorCycleReturnRequestUtc).TotalSeconds > 5 Then
                _lastDoorCycleReturnRequestUtc = DateTime.UtcNow
                ShowPrompt(
                    "A compartment is open. Close it first." &
                    Environment.NewLine &
                    "Press RETURN TO START again to confirm.")
                Return
            End If

            If _state = ScreenState.ValidatingCredential Then
                _returnToStartRequested = True
                CancelWorkflowRequests()
                ShowPrompt("Return to start requested. Finishing the current operation safely...")
                Return
            End If

            ResetToAwaitWorkflowChoice()
        End Sub
        Private Function CompleteDeferredReturnToStartIfRequested() As Boolean
            If Not _returnToStartRequested Then Return False

            ResetToAwaitWorkflowChoice()
            Return True
        End Function
        Private Sub BeginWorkflowCancellationScope()
            CancelWorkflowRequests()
            _workflowCancellation = New CancellationTokenSource()
        End Sub
        Private Sub CancelWorkflowRequests()
            If _workflowCancellation Is Nothing Then Return

            Try
                _workflowCancellation.Cancel()
            Catch
            Finally
                _workflowCancellation.Dispose()
                _workflowCancellation = Nothing
            End Try
        End Sub
        Private Function GetWorkflowCancellationToken() As CancellationToken
            If _workflowCancellation Is Nothing OrElse _workflowCancellation.IsCancellationRequested Then
                _workflowCancellation = New CancellationTokenSource()
            End If

            Return _workflowCancellation.Token
        End Function
        Private Sub StartWorkflowInactivityTimer()
            If _inactivityTimer Is Nothing Then
                _inactivityTimer = New Threading.DispatcherTimer With {
                    .Interval = TimeSpan.FromSeconds(1)
                }
                AddHandler _inactivityTimer.Tick, AddressOf WorkflowInactivityTimer_Tick
            End If

            _lastUserActivityUtc = DateTime.UtcNow
            _inactivityTimer.Start()
        End Sub
        Private Sub RecordUserActivity()
            _lastUserActivityUtc = DateTime.UtcNow
        End Sub
        Private Sub WorkflowInactivityTimer_Tick(sender As Object, e As EventArgs)
            If _state = ScreenState.AwaitWorkflowChoice Then Return

            Dim timeoutSeconds As Integer = Math.Max(15, AppSettings.WorkflowInactivityTimeoutSeconds)
            If (DateTime.UtcNow - _lastUserActivityUtc).TotalSeconds < timeoutSeconds Then Return

            _lastUserActivityUtc = DateTime.UtcNow
            TraceLogger.Log($"WORKFLOW INACTIVITY TIMEOUT state={_state}; timeoutSeconds={timeoutSeconds}")
            RequestReturnToStart()
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
            BeginWorkflowCancellationScope()

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
            ReturnToStartButton.Visibility = Visibility.Visible
            RecordUserActivity()

            Dim normalizedStepKey As String = (If(stepDef.StepKey, "")).Trim().ToLowerInvariant()

            Select Case normalizedStepKey
                Case "credential_scan"
                    PromptForCredential()

                Case "work_order_scan"
                    _state = ScreenState.AwaitWorkOrder
                    KeypadControl.Reset()
                    SetUiEnabled(True)

                    Dim promptText As String =
        If(String.IsNullOrWhiteSpace(stepDef.Prompt),
           $"Enter {GetReferenceLabel()}",
           stepDef.Prompt.Trim())

                    ShowPrompt(promptText)
                    FocusHidSink()

                Case "asset_scan"
                    PromptForAssetScan()

                Case "defect_decision"
                    PromptForDefectDecision()

                Case "size_selection"
                    _state = ScreenState.AwaitLockerSize
                    ShowLockerSizeSelection(If(String.IsNullOrWhiteSpace(stepDef.Prompt), "Select compartment size.", stepDef.Prompt))

                Case "compartment_assignment"
                    If _activeWorkflow.Mode = "asset_workflow" Then
                        ProcessAssetDepositAssignmentAsync()
                    ElseIf _activeWorkflow.Mode = "package_workflow" Then
                        ProcessPackageStageAssignmentAsync()
                    End If

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
            BeginWorkflowCancellationScope()


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
        Private Async Function SubmitCredentialAsync(rawCredential As String, source As String) As Task

            Dim myEpoch As Integer = _uiEpoch

            Dim credential As String = If(rawCredential, "").Trim()

            If String.IsNullOrWhiteSpace(credential) Then Return

            If _state = ScreenState.ValidatingCredential Then Return

            Dim nowUtc = DateTime.UtcNow

            If (nowUtc - _lastSubmitUtc) < _submitDebounce Then
                Return
            End If

            _lastSubmitUtc = nowUtc
            _lastCredentialKey = credential

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim isAdminFlow As Boolean =
        (_state = ScreenState.AwaitAdminCredential)

            Try

                _state = ScreenState.ValidatingCredential

                SetUiEnabled(False)

                _barcodeScanService.ResetAll()
                KeypadControl.Reset()

                ShowPrompt("Authorizing credential...")

                Dim purpose As AuthPurpose

                If isAdminFlow Then
                    purpose = AuthPurpose.AdminAccess
                ElseIf _activeWorkflow IsNot Nothing Then
                    purpose = GetPurposeForWorkflow(_activeWorkflow)
                Else
                    purpose = AuthPurpose.ValidateIdentity
                End If

                TraceLogger.Log(
            $"SUBMIT CREDENTIAL start actionId={actionId}; " &
            $"credentialKey={credential}; " &
            $"source={source}; purpose={purpose}; " &
            $"workflow={If(_activeWorkflow?.WorkflowKey, "<none>")}")

                Dim result As AuthResult =
            Await ValidateCredentialWithServerAsync(
                credential,
                purpose,
                source)

                If myEpoch <> _uiEpoch Then Return

                If result Is Nothing OrElse Not result.IsAuthorized Then

                    AuditAuthDenied(
                actionId,
                isAdminFlow,
                If(isAdminFlow,
                   "AdminAuthDenied",
                   "UserAuthDenied"))

                    Dim msg As String =
                If(result?.Message,
                   "Credential not recognized.")

                    ShowPrompt(msg & Environment.NewLine &
                       Environment.NewLine &
                       "Returning to start.")

                    Await Task.Delay(2500)

                    If myEpoch = _uiEpoch Then
                        ResetToAwaitWorkflowChoice()
                    End If

                    Return
                End If

                _authResult = result
                _authorizedWorkOrders =
                    If(result.WorkOrders, New List(Of WorkOrderAuthItem)()).
                    Where(Function(item) item IsNot Nothing).
                    ToList()

                If isAdminFlow Then

                    TraceLogger.Log(
                $"ADMIN AUTHORIZED actionId={actionId}")

                    Await ShowAdminPanelAsync(result)

                    If myEpoch = _uiEpoch Then
                        ResetToAwaitWorkflowChoice()
                    End If

                    Return
                End If

                TraceLogger.Log(
            $"AUTH SUCCESS actionId={actionId}; " &
            $"userId={result.UserId}; actorId={result.ActorID}")

                Await ContinueWorkflowAfterAuthAsync(result)

            Catch ex As Exception

                TraceExceptionDeep(
            "SUBMIT_CREDENTIAL_EXCEPTION",
            ex)

                TraceToFile(
            "AUTH_EXCEPTION: " &
            ex.GetType().FullName &
            " :: " &
            ex.Message)

                AuditAuthError(
            actionId,
            isAdminFlow,
            "AuthException:" & ex.GetType().Name)

                ShowPrompt(
            "Authorization failed. Returning to start.")

                Dim resetOperation = Dispatcher.BeginInvoke(
                    Async Sub()
                        Await Task.Delay(3000)
                        If myEpoch = _uiEpoch Then
                            ResetToAwaitWorkflowChoice()
                        End If
                    End Sub)

                Return

            Finally

                If myEpoch = _uiEpoch Then
                    If Not CompleteDeferredReturnToStartIfRequested() Then
                        SetUiEnabled(True)
                        KeypadControl.Reset()
                        _barcodeScanService.ResetAll()
                        FocusHidSink()
                    End If

                End If

            End Try

        End Function

        'debug helper to log detailed exception info, including inner exceptions and specific handling for common reflection and file exceptions that may occur during auth validation
        Private Sub TraceExceptionDeep(label As String, ex As Exception)
            Try
                TraceToFile("========== " & label & " ==========")

                Dim current As Exception = ex
                Dim level As Integer = 0

                While current IsNot Nothing
                    TraceToFile($"[{level}] Type: {current.GetType().FullName}")
                    TraceToFile($"[{level}] Message: {current.Message}")
                    TraceToFile($"[{level}] Source: {current.Source}")

                    If current.TargetSite IsNot Nothing Then
                        TraceToFile($"[{level}] TargetSite: {current.TargetSite.DeclaringType?.FullName}.{current.TargetSite.Name}")
                    End If

                    TraceToFile($"[{level}] StackTrace:")
                    TraceToFile(current.StackTrace)

                    Dim fileEx = TryCast(current, System.IO.FileNotFoundException)
                    If fileEx IsNot Nothing Then
                        TraceToFile($"[{level}] FileName: {fileEx.FileName}")
                        TraceToFile($"[{level}] FusionLog: {fileEx.FusionLog}")
                    End If

                    Dim loadEx = TryCast(current, Reflection.ReflectionTypeLoadException)
                    If loadEx IsNot Nothing Then
                        TraceToFile($"[{level}] LoaderExceptions:")
                        For Each loaderEx In loadEx.LoaderExceptions
                            TraceExceptionDeep("LOADER_EXCEPTION", loaderEx)
                        Next
                    End If

                    current = current.InnerException
                    level += 1
                End While

                TraceToFile("========== END " & label & " ==========")

            Catch traceEx As Exception
                MessageBox.Show("Failed while tracing exception: " & traceEx.Message)
            End Try
        End Sub
        Private Async Function ValidateCredentialWithServerAsync(
    scanValue As String,
    purpose As AuthPurpose,
    source As String
) As Task(Of AuthResult)

            Try
                Return Await _backend.AuthorizeAsync(
                    scanValue,
                    purpose,
                    source,
                    GetWorkflowCancellationToken())

            Catch ex As OperationCanceledException
                TraceExceptionDeep("AUTH_CANCELLED", ex)

                Return New AuthResult With {
            .IsAuthorized = False,
            .Purpose = purpose,
            .Message = "Authorization request was cancelled."
        }

            Catch ex As Exception
                TraceExceptionDeep("AUTH_FAILED", ex)

                Return New AuthResult With {
            .IsAuthorized = False,
            .Purpose = purpose,
            .Message = "Authorization failed."
        }
            End Try

        End Function
        Private Function GetPurposeForWorkflow(workflow As WorkflowDefinition) As AuthPurpose

            If workflow Is Nothing Then
                Throw New InvalidOperationException("No active workflow is selected.")
            End If

            Dim mode As String =
        If(workflow.Mode, "").Trim().ToLowerInvariant()

            Dim action As String =
        GetWorkflowAction(workflow)

            Select Case mode

                Case "package_workflow"

                    If action = "stage" Then
                        Return AuthPurpose.DeliveryCourierAuth
                    End If

                    If action = "pickup" Then
                        Return AuthPurpose.PickupAccess
                    End If

                    Return AuthPurpose.ValidateIdentity

                Case "asset_workflow"

                    If action = "pickup" Then
                        Return AuthPurpose.PickupAccess
                    End If

                    If action = "stage" Then
                        Return AuthPurpose.DeliveryCourierAuth
                    End If

                    Return AuthPurpose.ValidateIdentity

                Case "day_use"
                    Return AuthPurpose.DayUseStart

                Case Else
                    Throw New InvalidOperationException($"Unknown workflow mode '{workflow.Mode}'.")

            End Select

        End Function
        Private Async Function ContinueWorkflowAfterAuthAsync(result As AuthResult) As Task

            If result Is Nothing Then
                Throw New InvalidOperationException("Authorization result is missing.")
            End If

            If _activeWorkflow Is Nothing Then
                Throw New InvalidOperationException("No active workflow is selected.")
            End If

            If _activeStep Is Nothing Then
                Throw New InvalidOperationException("No active workflow step is selected.")
            End If

            Dim currentStepKey As String =
        If(_activeStep.StepKey, "").Trim().ToLowerInvariant()

            If currentStepKey <> "credential_scan" Then
                Throw New InvalidOperationException(
            $"ContinueWorkflowAfterAuthAsync was called while current step was '{_activeStep.StepKey}' instead of 'credential_scan'.")
            End If

            Dim mode As String =
        If(_activeWorkflow.Mode, "").Trim().ToLowerInvariant()

            If mode = "package_workflow" OrElse mode = "asset_workflow" Then
                _courierAuth = result
            End If

            If Not IsCredentialAllowedForActiveWorkflow(result) Then

                ShowPrompt(
            "Credential is not authorized for this workflow." &
            Environment.NewLine &
            Environment.NewLine &
            "Returning to start.")

                Await Task.Delay(2500)

                ResetToAwaitWorkflowChoice()
                Return

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
    .ActorId = ActorId,
    .LockerController = _lockerController
}

            w.ShowDialog()
            FocusHidSink()

            Return Task.CompletedTask
        End Function
        Private Function IsCredentialAllowedForActiveWorkflow(result As AuthResult) As Boolean

            If result Is Nothing OrElse Not result.IsAuthorized Then
                Return False
            End If

            Dim requiredRolesRaw As String =
        GetWorkflowOption("RequiredCredentialRoles", "")

            If String.IsNullOrWhiteSpace(requiredRolesRaw) Then
                Return True
            End If

            Dim requiredRoles As List(Of String) =
        requiredRolesRaw.Split(","c).
            Select(Function(r) r.Trim()).
            Where(Function(r) r.Length > 0).
            ToList()

            If requiredRoles.Count = 0 Then
                Return True
            End If

            If result.Roles Is Nothing OrElse result.Roles.Count = 0 Then
                Return False
            End If

            Return result.Roles.Any(
        Function(userRole)
            Return requiredRoles.Any(
                Function(requiredRole)
                    Return String.Equals(
                        userRole.Trim(),
                        requiredRole.Trim(),
                        StringComparison.OrdinalIgnoreCase)
                End Function)
        End Function)

        End Function
        Private Async Function AuthorizeLockerOpenActionForAssetAsync(
    assetTag As String,
    lockerNumber As String,
    correlationId As String,
    sessionUserId As String,
    ct As CancellationToken
) As Task(Of LockerAuthorizeResponseDto)

            Dim cleanAssetTag As String = If(assetTag, "").Trim()
            Dim cleanLockerNumber As String = If(lockerNumber, "").Trim()
            Dim cleanActorId As String = If(sessionUserId, "").Trim()
            Dim cleanCorrelationId As String = If(correlationId, "").Trim()

            If String.IsNullOrWhiteSpace(cleanAssetTag) Then
                Throw New ArgumentException("assetTag is required.", NameOf(assetTag))
            End If

            If String.IsNullOrWhiteSpace(cleanLockerNumber) Then
                Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
            End If

            If String.IsNullOrWhiteSpace(cleanActorId) Then
                Throw New InvalidOperationException("ActorId is required before authorizing asset locker action.")
            End If

            If String.IsNullOrWhiteSpace(cleanCorrelationId) Then
                cleanCorrelationId = Guid.NewGuid().ToString("N")
            End If

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            AsNoTracking().
            SingleOrDefault(Function(l) l.LockerNumber = cleanLockerNumber)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {cleanLockerNumber} was not found.")
                End If

                Dim siteCode As String =
            If(String.IsNullOrWhiteSpace(AppSettings.SiteCode),
               AppSettings.LocationId,
               AppSettings.SiteCode)

                Dim workflowKey As String = GetActiveWorkflowKey()
                Dim workflowAction As String = GetWorkflowAction(_activeWorkflow)

                Dim dto As New LockerAuthorizeRequestDto With {
            .requestId = Guid.NewGuid().ToString("N"),
            .correlationId = cleanCorrelationId,
            .requestedBy = cleanActorId,
            .actorId = cleanActorId,
            .requestedByType = "user",
            .siteCode = siteCode,
            .lockerBankId = $"BANK-{locker.Branch}",
            .lockerId = locker.LockerNumber,
            .doorId = $"D{locker.RelayId}",
            .actionType = "open_door",
            .requestedAtUtc = DateTime.UtcNow.ToString("o"),
            .reasonCode = "ASSET_DEPOSIT",
            .metadata = New LockerAuthorizeMetadataDto With {
                .workOrderId = cleanAssetTag
            }
        }

                TraceLogger.Log(
            "ASSET LOCKER AUTHORIZE REQUEST: " &
            "actorId=" & cleanActorId &
            "; requestedBy=" & cleanActorId &
            "; assetTag=" & cleanAssetTag &
            "; locker=" & cleanLockerNumber &
            "; workflow=" & workflowKey &
            "; workflowAction=" & workflowAction &
            "; correlationId=" & cleanCorrelationId)

                Return Await _backend.AuthorizeLockerActionAsync(
            dto,
            _sessionToken,
            ct)

            End Using

        End Function


#End Region

#Region "Pickup workflow"
        Private Async Function SubmitWorkOrderAsync(rawWorkOrder As String, source As String) As Task

            Dim myEpoch As Integer = _uiEpoch

            Dim referenceNumber As String = (If(rawWorkOrder, "")).Trim()
            If referenceNumber.Length = 0 Then Return

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

            ShowPrompt($"Processing {GetReferenceLabel().ToLowerInvariant()} {referenceNumber}…")

            Dim processException As Exception = Nothing

            Try
                Dim mode As String = If(_activeWorkflow.Mode, "").Trim().ToLowerInvariant()
                Dim action As String = GetWorkflowAction(_activeWorkflow)

                If mode = "package_workflow" AndAlso action = "pickup" Then
                    Await ProcessPickupWorkOrderAsync(referenceNumber, source, actionId, myEpoch)
                    Return
                End If

                If mode = "package_workflow" AndAlso action = "stage" Then

                    _activeWorkOrder = New WorkOrderAuthItem With {
                .WorkOrderNumber = referenceNumber,
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
                .ReasonCode = $"PackageReferenceCaptured;Reference={referenceNumber};Source={source};Workflow={GetActiveWorkflowKey()}"
            })

                    If myEpoch <> _uiEpoch Then Return

                    AdvanceToNextStep()
                    Return

                End If

                ShowPrompt("Workflow is not configured correctly. Returning to start.")

                Await Task.Delay(2500)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            Catch ex As Exception
                processException = ex

            Finally
                If myEpoch = _uiEpoch Then
                    If Not CompleteDeferredReturnToStartIfRequested() Then
                        KeypadControl.Reset()
                        SetUiEnabled(True)
                        FocusHidSink()
                    End If
                End If
            End Try

            If processException IsNot Nothing Then

                If myEpoch <> _uiEpoch Then Return

                TraceExceptionDeep("SUBMIT_REFERENCE_FAILED", processException)

                ShowPrompt("System unavailable. Returning to start.")

                Await Task.Delay(3000)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            End If

        End Function
        Private Function GetWorkflowAction(workflow As WorkflowDefinition) As String

            If workflow Is Nothing Then Return String.Empty

            If Not String.IsNullOrWhiteSpace(workflow.WorkflowAction) Then
                Return workflow.WorkflowAction.Trim().ToLowerInvariant()
            End If

            ' Backward-compatible fallback for current hard-coded workflow keys.
            Dim key As String = If(workflow.WorkflowKey, "").Trim().ToLowerInvariant()

            If key.Length = 0 Then Return String.Empty

            If key.Contains("pickup") OrElse
       key.Contains("retrieve") OrElse
       key.Contains("checkout") Then

                Return "pickup"

            End If

            If key.Contains("stage") OrElse
       key.Contains("deposit") OrElse
       key.Contains("delivery") OrElse
       key.Contains("checkin") OrElse
       key.Contains("check-in") Then

                Return "stage"

            End If

            Return String.Empty

        End Function
        Private Function GetReferenceLabel() As String

            Dim configuredLabel As String =
        GetWorkflowOption("ReferenceLabel", "")

            If Not String.IsNullOrWhiteSpace(configuredLabel) Then
                Return configuredLabel.Trim()
            End If

            If _activeStep IsNot Nothing AndAlso
       Not String.IsNullOrWhiteSpace(_activeStep.Prompt) Then

                Dim prompt As String = _activeStep.Prompt.Trim()

                If prompt.Contains("DoD", StringComparison.OrdinalIgnoreCase) Then
                    Return "Airman DoD Number"
                End If

                If prompt.Contains("Work Order", StringComparison.OrdinalIgnoreCase) Then
                    Return "Work Order"
                End If

            End If

            Return "Reference"

        End Function
        Private Function FindAuthorizedWorkOrder(scannedWorkOrder As String) As WorkOrderAuthItem

            Dim wo = (If(scannedWorkOrder, "")).Trim()

            If wo.Length = 0 Then
                Return Nothing
            End If

            Dim requiresReferenceAuthorization As Boolean =
        GetWorkflowOptionBoolean("RequiresReferenceAuthorization", True)

            Dim desiredTxn As String =
        If(IsPackageStageWorkflow(), "Delivery", "Pickup")

            ' -------------------------------------------------
            ' Reference-only workflows (ex: Shaw/IPE pickup)
            ' -------------------------------------------------
            If Not requiresReferenceAuthorization Then

                Return New WorkOrderAuthItem With {
            .WorkOrderNumber = wo,
            .TransactionType = desiredTxn,
            .LockerNumber = "",
            .AllowedSizeCode = ""
        }

            End If

            ' -------------------------------------------------
            ' Authorized work-order workflows
            ' -------------------------------------------------
            If _authorizedWorkOrders Is Nothing OrElse
       _authorizedWorkOrders.Count = 0 Then

                Return Nothing

            End If

            Dim matches = _authorizedWorkOrders.
        Where(Function(x) x IsNot Nothing AndAlso
                          wo.Equals(
                              (If(x.WorkOrderNumber, "")).Trim(),
                              StringComparison.OrdinalIgnoreCase) AndAlso
                          (String.IsNullOrWhiteSpace(x.TransactionType) OrElse
                           desiredTxn.Equals(
                               x.TransactionType.Trim(),
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

            Dim target As String = wo.ToUpperInvariant()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim candidates = db.Lockers.
            AsNoTracking().
            Include(Function(l) l.Status).
            Where(Function(l) l.IsEnabled).
            Where(Function(l) l.Status IsNot Nothing).
            Where(Function(l) l.Status.OccupancyState = OccupancyState.Reserved OrElse
                              l.Status.OccupancyState = OccupancyState.Occupied).
            OrderBy(Function(l) l.RelayId).
            ThenBy(Function(l) l.LockerNumber).
            ToList()

                Dim lockerNumbers = candidates.
            Where(Function(l)
                      Dim reservedRef As String =
                          If(l.Status.ReservedWorkOrderNumber, "").Trim().ToUpperInvariant()

                      Dim lastRef As String =
                          If(l.Status.LastWorkOrderNumber, "").Trim().ToUpperInvariant()

                      Return reservedRef = target OrElse lastRef = target
                  End Function).
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

            Dim wo As String = workOrderNumber.Trim()
            Dim processException As Exception = Nothing
            Dim openResults As New List(Of LockerActionResult)
            Dim successfullyOpenedLockerNumbers As New List(Of String)

            Try
                Dim authorized = FindAuthorizedWorkOrder(wo)

                If authorized Is Nothing Then
                    If myEpoch <> _uiEpoch Then Return

                    ShowPrompt($"{GetReferenceLabel()} {wo} not recognized.")
                    _state = ScreenState.AwaitWorkOrder
                    Return
                End If

                _activeWorkOrder = authorized

                Dim lockerNumbers = GetReservedLockerNumbersForWorkOrder(wo)

                If lockerNumbers Is Nothing OrElse lockerNumbers.Count = 0 Then
                    If myEpoch <> _uiEpoch Then Return

                    ShowPrompt($"No package found for {GetReferenceLabel().ToLowerInvariant()} {wo}.")
                    _state = ScreenState.AwaitWorkOrder
                    Return
                End If

                ShowPrompt($"Opening {lockerNumbers.Count} compartment(s)...")

                For Each lockerNumber In lockerNumbers

                    If myEpoch <> _uiEpoch Then Return

                    Dim openResult =
                Await TryOpenLockerWithJournalAsync(actionId, wo, lockerNumber)

                    If openResult IsNot Nothing Then
                        openResults.Add(openResult)
                    End If

                    If openResult IsNot Nothing AndAlso openResult.Success Then
                        successfullyOpenedLockerNumbers.Add(lockerNumber)
                    Else
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

                    Await Task.Delay(200)

                Next

                If myEpoch <> _uiEpoch Then Return

                If successfullyOpenedLockerNumbers.Count = 0 Then
                    ShowPrompt("No compartments could be opened. Please contact attendant.")

                    Await Task.Delay(3000)

                    If myEpoch = _uiEpoch Then
                        ResetToAwaitWorkflowChoice()
                    End If

                    Return
                End If

                ShowPrompt("Please remove items and close all opened compartments.")

                For Each lockerNumber In successfullyOpenedLockerNumbers

                    Dim closedOk As Boolean =
                Await WaitForLockerClosedAsync(lockerNumber, 120000, myEpoch)

                    If myEpoch <> _uiEpoch Then Return

                    If Not closedOk Then
                        ShowPrompt($"Please close compartment {lockerNumber}. Pickup was not completed.")

                        For Each openResult In openResults
                            If openResult IsNot Nothing Then
                                Try
                                    Await New LockerActionService().
                                UpdateJournalStateAsync(
                                    openResult.JournalId,
                                    LockerTransactionState.NeedsReconciliation,
                                    LockerAckStatus.Failed,
                                    $"Door close timeout during pickup. Locker={lockerNumber}")
                                Catch updateEx As Exception
                                    TraceExceptionDeep("PICKUP_JOURNAL_CLOSE_TIMEOUT_UPDATE_FAILED", updateEx)
                                End Try
                            End If
                        Next

                        Await Task.Delay(3000)

                        If myEpoch = _uiEpoch Then
                            ResetToAwaitWorkflowChoice()
                        End If

                        Return
                    End If

                Next

                Await CompletePickupForLockersAsync(wo, successfullyOpenedLockerNumbers, openResults)

                If myEpoch <> _uiEpoch Then Return

                ShowPrompt("Pickup complete.")

                Await Task.Delay(1500)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            Catch ex As Exception
                processException = ex
            End Try

            If processException IsNot Nothing Then

                TraceExceptionDeep("PICKUP_PROCESS_FAILED", processException)

                For Each openResult In openResults

                    If openResult IsNot Nothing Then

                        Try
                            Await New LockerActionService().
                        UpdateJournalStateAsync(
                            openResult.JournalId,
                            LockerTransactionState.NeedsReconciliation,
                            LockerAckStatus.Failed,
                            processException.Message)

                        Catch updateEx As Exception
                            TraceExceptionDeep("PICKUP_JOURNAL_UPDATE_FAILED", updateEx)
                        End Try

                    End If

                Next

                ShowPrompt("Pickup failed. Returning to start.")

                Await Task.Delay(3000)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

                Return

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

            If _activeWorkflow Is Nothing Then
                ResetToAwaitWorkflowChoice()
                Return
            End If

            Dim mode As String = If(_activeWorkflow.Mode, "").Trim().ToLowerInvariant()

            Select Case mode

                Case "asset_workflow"
                    SetActiveStep("asset_scan")

                Case "package_workflow"
                    SetActiveStep("work_order_scan")

                Case Else
                    ResetToAwaitWorkflowChoice()
                    Return

            End Select

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
        Private Async Sub BeginAssetCheckoutAsync()

            Dim myEpoch As Integer = _uiEpoch
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim candidate As AssetCheckoutCandidate = ResolveNextCheckoutCandidate()

            If candidate Is Nothing OrElse
       String.IsNullOrWhiteSpace(candidate.LockerNumber) OrElse
       String.IsNullOrWhiteSpace(candidate.AssetTag) OrElse
       String.IsNullOrWhiteSpace(candidate.DeviceType) Then

                ShowPrompt("No authorized device is currently available for this user.")
                Await Task.Delay(1500)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

                Return
            End If

            Await ProcessCheckoutAsync(candidate, actionId, myEpoch)

        End Sub
        Private Async Function CompleteCheckoutForLockerAsync(deviceType As String,
                                                      lockerNumber As String,
                                                      Optional journalId As Integer? = Nothing) As Task

            Dim requestedType As String = If(deviceType, "").Trim()
            Dim ln As String = If(lockerNumber, "").Trim()

            If requestedType.Length = 0 OrElse ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Return
                End If

                Dim actorId As String = ""

                If _authResult IsNot Nothing Then
                    actorId = If(_authResult.ActorID, _authResult.UserId)
                End If

                locker.Status.OccupancyState = OccupancyState.Vacant
                locker.Status.PackagePresent = False
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "CheckoutCompleted"
                locker.Status.LastActorId = If(actorId, "").Trim()

                locker.Status.LastWorkOrderNumber = ""
                locker.Status.ReservedWorkOrderNumber = ""
                locker.Status.ReservedCorrelationId = ""
                locker.Status.ReservedUntilUtc = Nothing

                locker.Status.CurrentDeviceType = ""
                locker.Status.CurrentAssetTag = ""
                locker.Status.IsDefectiveHold = False
                locker.Status.DefectType = ""

                Try
                    db.SaveChanges()

                Catch ex As Exception
                    TraceExceptionDeep("CHECKOUT_COMPLETE_DB_SAVE_FAILED", ex)
                    Throw
                End Try

            End Using

            If journalId.HasValue Then
                Await New LockerActionService().
            MarkDoorClosedAndLocalStateUpdatedAsync(journalId.Value)
            End If

        End Function
        Private Async Function ProcessCheckoutAsync(
    candidate As AssetCheckoutCandidate,
    actionId As String,
    myEpoch As Integer
) As Task

            Dim processException As Exception = Nothing
            Dim openResult As LockerActionResult = Nothing
            Dim authorizeResponse As LockerAuthorizeResponseDto = Nothing
            Dim ackSucceeded As Boolean = False

            Try
                If candidate Is Nothing Then
                    ShowPrompt("No checkout candidate was selected.")
                    Await Task.Delay(1500)

                    If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                    Return
                End If

                Dim lockerNumber As String = If(candidate.LockerNumber, "").Trim()
                Dim assetTag As String = If(candidate.AssetTag, "").Trim()
                Dim requestedType As String = If(candidate.DeviceType, "").Trim().ToUpperInvariant()

                If lockerNumber.Length = 0 OrElse assetTag.Length = 0 OrElse requestedType.Length = 0 Then
                    ShowPrompt("Selected checkout record is incomplete.")
                    Await Task.Delay(1500)

                    If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                    Return
                End If

                _state = ScreenState.ValidatingCredential
                SetUiEnabled(False)

                Dim credentialKey As String = If(_lastCredentialKey, "").Trim()

                If credentialKey.Length = 0 AndAlso _authResult IsNot Nothing Then
                    credentialKey = If(_authResult.UserId, "").Trim()
                End If

                If credentialKey.Length = 0 Then
                    credentialKey = GetAuthorizedActorIdForCurrentSession()
                End If

                If credentialKey.Length = 0 Then
                    Throw New InvalidOperationException("CredentialKey is required before checkout validation.")
                End If

                TraceLogger.Log(
            "CHECKOUT VALIDATION START: " &
            "credentialKey=" & credentialKey &
            "; assetTag=" & assetTag &
            "; locker=" & lockerNumber &
            "; workflow=asset-checkout")

                ShowPrompt("Validating checkout.")

                Dim opsBackend = TryCast(_backend, OperationsBackendService)
                Dim validation As AssetValidateResponse

                If opsBackend IsNot Nothing Then
                    validation = Await opsBackend.ValidateAssetAsync(
                assetTag:=assetTag,
                credentialKey:=credentialKey,
                workflow:="asset-checkout",
                workflowAction:="pickup",
                ct:=GetWorkflowCancellationToken())
                Else
                    validation = Await _backend.ValidateAssetAsync(
                        assetTag,
                        GetWorkflowCancellationToken())
                End If

                If myEpoch <> _uiEpoch Then Return

                If validation Is Nothing OrElse Not validation.isValid Then
                    Dim msg As String = If(validation?.message, "").Trim()
                    If msg.Length = 0 Then msg = "Checkout is not authorized for this associate."

                    TraceLogger.Log(
                "CHECKOUT VALIDATION DENIED: " &
                "assetTag=" & assetTag &
                "; locker=" & lockerNumber &
                "; message=" & msg)

                    ShowPrompt(msg & Environment.NewLine & Environment.NewLine & "No compartment was opened.")
                    Await Task.Delay(3000)

                    If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                    Return
                End If

                TraceLogger.Log(
            "CHECKOUT VALIDATION ACCEPTED: " &
            "assetTag=" & assetTag &
            "; locker=" & lockerNumber &
            "; deviceType=" & requestedType)

                ShowPrompt("Authorizing compartment release.")
                authorizeResponse =
    Await AuthorizeCheckoutWithBackendAsync(
        assetTag:=assetTag,
        lockerNumber:=lockerNumber,
        credentialKey:=credentialKey,
        actionId:=actionId)

                If myEpoch <> _uiEpoch Then Return


                TraceLogger.Log(
    "CHECKOUT BACKEND AUTHORIZED: " &
    "assetTag=" & assetTag &
    "; locker=" & lockerNumber &
    "; transactionId=" &
    If(authorizeResponse?.transactionId, "<NULL>"))

                ShowPrompt($"Opening {requestedType} compartment.")

                openResult = Await TryOpenLockerWithJournalAsync(
            actionId,
            assetTag,
            lockerNumber)

                If myEpoch <> _uiEpoch Then Return

                If openResult Is Nothing OrElse Not openResult.Success Then
                    ShowPrompt($"Compartment {lockerNumber} could not be opened.")
                    Await Task.Delay(3000)

                    If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                    Return
                End If

                ShowPrompt($"Compartment {lockerNumber} opened. Remove device and close the door.")

                Dim closedOk As Boolean =
            Await WaitForLockerClosedAsync(
                lockerNumber:=lockerNumber,
                timeoutMs:=120000,
                myEpoch:=myEpoch)

                If myEpoch <> _uiEpoch Then Return

                If Not closedOk Then
                    ShowPrompt($"Please close compartment {lockerNumber}. Checkout was not completed.")
                    Await Task.Delay(3000)

                    If myEpoch = _uiEpoch Then ResetToAwaitWorkflowChoice()
                    Return
                End If

                ShowPrompt("Completing checkout with backend.")

                ackSucceeded =
    Await CompleteCheckoutLocalStateAndAckBackendAsync(
        deviceType:=requestedType,
        lockerNumber:=lockerNumber,
        assetTag:=assetTag,
        actionId:=actionId,
        authorizeResponse:=authorizeResponse,
        journalId:=openResult.JournalId)

                If myEpoch <> _uiEpoch Then Return

                If Not ackSucceeded Then
                    ShowPrompt($"{requestedType} checked out. Backend sync is pending.")
                    Await Task.Delay(2500)

                    If myEpoch = _uiEpoch Then
                        ResetToAwaitWorkflowChoice()
                    End If

                    Return
                End If

                ShowPrompt($"{requestedType} checked out successfully.")

                Await Task.Delay(1500)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            Catch ex As Exception
                processException = ex
            End Try

            If processException IsNot Nothing Then

                TraceExceptionDeep("CHECKOUT_PROCESS_FAILED", processException)

                If openResult IsNot Nothing Then
                    Try
                        Await New LockerActionService().
                    UpdateJournalStateAsync(
                        openResult.JournalId,
                        LockerTransactionState.NeedsReconciliation,
                        LockerAckStatus.Failed,
                        processException.Message)
                    Catch updateEx As Exception
                        TraceExceptionDeep("CHECKOUT_JOURNAL_UPDATE_FAILED", updateEx)
                    End Try
                End If

                ShowPrompt("Checkout failed. Returning to start.")

                Await Task.Delay(3000)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            End If

        End Function
        Private Function ResolveNextCheckoutCandidate() As AssetCheckoutCandidate

            If _authResult Is Nothing Then
                TraceLogger.Log("CHECKOUT: _authResult is Nothing.")
                Return Nothing
            End If

            If _authResult.AuthorizedDevices Is Nothing OrElse _authResult.AuthorizedDevices.Count = 0 Then
                TraceLogger.Log("CHECKOUT: No AuthorizedDevices found on AuthResult.")
                Return Nothing
            End If

            Dim authorizedTypes =
        _authResult.AuthorizedDevices.
            Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
            Select(Function(x) x.Trim().ToUpperInvariant()).
            Distinct().
            ToList()

            Using db = DatabaseBootstrapper.BuildDbContext()

                For Each deviceType In authorizedTypes

                    TraceLogger.Log("CHECKOUT: Looking for occupied locker with device type " & deviceType)

                    Dim locker = db.Lockers.
                Include(Function(l) l.Status).
                Where(Function(l) l.IsEnabled).
                Where(Function(l) l.Status IsNot Nothing).
                Where(Function(l) l.Status.OccupancyState = OccupancyState.Occupied).
                Where(Function(l) l.Status.PackagePresent = True).
                Where(Function(l) Not String.IsNullOrWhiteSpace(l.Status.CurrentAssetTag)).
                Where(Function(l) Not String.IsNullOrWhiteSpace(l.Status.CurrentDeviceType)).
                Where(Function(l) l.Status.CurrentDeviceType.ToUpper() = deviceType).
                OrderBy(Function(l) l.RelayId).
                ThenBy(Function(l) l.LockerNumber).
                FirstOrDefault()

                    If locker IsNot Nothing AndAlso locker.Status IsNot Nothing Then

                        Dim candidate As New AssetCheckoutCandidate With {
                    .LockerNumber = If(locker.LockerNumber, "").Trim(),
                    .AssetTag = If(locker.Status.CurrentAssetTag, "").Trim(),
                    .DeviceType = If(locker.Status.CurrentDeviceType, "").Trim().ToUpperInvariant()
                }

                        TraceLogger.Log(
                    "CHECKOUT: Candidate selected. " &
                    "locker=" & candidate.LockerNumber &
                    "; assetTag=" & candidate.AssetTag &
                    "; deviceType=" & candidate.DeviceType)

                        Return candidate

                    End If

                Next

            End Using

            TraceLogger.Log("CHECKOUT: Authorized devices found, but no occupied locker matched.")
            Return Nothing

        End Function
        Private Async Function ClearLocalCheckoutOccupancyAfterBackendAckAsync(
    deviceType As String,
    lockerNumber As String,
    Optional journalId As Integer? = Nothing
) As Task

            Dim requestedType As String = If(deviceType, "").Trim()
            Dim ln As String = If(lockerNumber, "").Trim()

            If requestedType.Length = 0 OrElse ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Return
                End If

                Dim actorId As String = ""

                If _authResult IsNot Nothing Then
                    actorId = If(_authResult.ActorID, _authResult.UserId)
                End If

                locker.Status.OccupancyState = OccupancyState.Vacant
                locker.Status.PackagePresent = False
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "CheckoutCompletedBackendAcked"
                locker.Status.LastActorId = If(actorId, "").Trim()

                locker.Status.LastWorkOrderNumber = ""
                locker.Status.ReservedWorkOrderNumber = ""
                locker.Status.ReservedCorrelationId = ""
                locker.Status.ReservedUntilUtc = Nothing

                locker.Status.CurrentDeviceType = ""
                locker.Status.CurrentAssetTag = ""
                locker.Status.IsDefectiveHold = False
                locker.Status.DefectType = ""

                Try
                    db.SaveChanges()
                Catch ex As Exception
                    TraceExceptionDeep("CHECKOUT_LOCAL_CLEAR_SAVE_FAILED", ex)
                    Throw
                End Try

            End Using

            If journalId.HasValue Then
                Await New LockerActionService().
            MarkDoorClosedAndLocalStateUpdatedAsync(journalId.Value)
            End If

        End Function

        Private Function GetCurrentBackendBearerToken() As String

            If Not String.IsNullOrWhiteSpace(_sessionToken) Then
                Return _sessionToken.Trim()
            End If

            If _courierAuth IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_courierAuth.SessionToken) Then
                Return _courierAuth.SessionToken.Trim()
            End If

            If _authResult IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_authResult.SessionToken) Then
                Return _authResult.SessionToken.Trim()
            End If

            Return ""

        End Function

        Private Async Function AuthorizeCheckoutWithBackendAsync(
    assetTag As String,
    lockerNumber As String,
    credentialKey As String,
    actionId As String
) As Task(Of LockerAuthorizeResponseDto)

            Dim opsBackend = TryCast(_backend, OperationsBackendService)

            If opsBackend Is Nothing Then
                Throw New InvalidOperationException(
            "Operations backend is required for checkout authorization.")
            End If

            Dim actorId As String = ""

            If _authResult IsNot Nothing Then
                actorId = If(_authResult.ActorID, "").Trim()

                If actorId.Length = 0 Then
                    actorId = If(_authResult.UserId, "").Trim()
                End If
            End If

            If actorId.Length = 0 Then
                actorId = credentialKey
            End If

            Dim cleanAssetTag As String = If(assetTag, "").Trim()
            Dim cleanLockerNumber As String = If(lockerNumber, "").Trim()
            Dim cleanCredentialKey As String = If(credentialKey, "").Trim()
            Dim cleanActionId As String = If(actionId, "").Trim()

            If cleanAssetTag.Length = 0 Then
                Throw New ArgumentException("assetTag is required.", NameOf(assetTag))
            End If

            If cleanLockerNumber.Length = 0 Then
                Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
            End If

            If cleanActionId.Length = 0 Then
                cleanActionId = Guid.NewGuid().ToString("N")
            End If

            If actorId.Length = 0 Then
                actorId = cleanCredentialKey
            End If

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            AsNoTracking().
            SingleOrDefault(Function(l) l.LockerNumber = cleanLockerNumber)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {cleanLockerNumber} was not found.")
                End If

                Dim siteCode As String =
            If(String.IsNullOrWhiteSpace(AppSettings.SiteCode),
               AppSettings.LocationId,
               AppSettings.SiteCode)

                Dim dto As New LockerAuthorizeRequestDto With {
            .requestId = Guid.NewGuid().ToString("N"),
            .correlationId = cleanActionId,
            .requestedBy = actorId,
            .actorId = actorId,
            .requestedByType = "user",
            .siteCode = siteCode,
            .lockerBankId = $"BANK-{locker.Branch}",
            .lockerId = locker.LockerNumber,
            .doorId = $"D{locker.RelayId}",
            .actionType = "open_door",
            .requestedAtUtc = DateTime.UtcNow.ToString("o"),
            .reasonCode = "ASSET_CHECKOUT",
            .metadata = New LockerAuthorizeMetadataDto With {
                .workOrderId = cleanAssetTag
            }
        }

                Dim bearerToken As String = GetCurrentBackendBearerToken()

                TraceLogger.Log(
            "CHECKOUT LOCKER AUTHORIZE START: " &
            "locker=" & cleanLockerNumber &
            "; assetTag=" & cleanAssetTag &
            "; actorId=" & actorId &
            "; requestedBy=" & actorId &
            "; tokenPresent=" & Not String.IsNullOrWhiteSpace(bearerToken) &
            "; correlationId=" & cleanActionId)

                Dim response =
            Await opsBackend.AuthorizeLockerActionAsync(
                dto,
                bearerToken:=bearerToken,
                ct:=GetWorkflowCancellationToken())

            If response Is Nothing Then
                Throw New InvalidOperationException(
            "Locker authorize response was null.")
            End If

            If response.authorization Is Nothing OrElse
       Not response.authorization.isAuthorized Then

                Throw New InvalidOperationException(
            "Backend denied locker checkout authorization.")
            End If

            If String.IsNullOrWhiteSpace(response.transactionId) Then
                Throw New InvalidOperationException(
            "Locker authorize response missing transactionId.")
            End If

                If String.IsNullOrWhiteSpace(response.commandId) Then
                    Throw New InvalidOperationException(
                "Locker authorize response missing commandId.")
                End If

            TraceLogger.Log(
        "CHECKOUT LOCKER AUTHORIZED: " &
        "transactionId=" & response.transactionId &
        "; commandId=" & response.commandId &
        "; locker=" & cleanLockerNumber)

            Return response

            End Using

        End Function

        Private Async Function CompleteCheckoutLocalStateAndAckBackendAsync(
    deviceType As String,
    lockerNumber As String,
    assetTag As String,
    actionId As String,
    authorizeResponse As LockerAuthorizeResponseDto,
    Optional journalId As Integer? = Nothing
) As Task(Of Boolean)

            If authorizeResponse Is Nothing Then
                Throw New ArgumentNullException(NameOf(authorizeResponse))
            End If

            Dim opsBackend = TryCast(_backend, OperationsBackendService)

            If opsBackend Is Nothing Then
                Throw New InvalidOperationException(
            "Operations backend is required for checkout ACK.")
            End If

            Dim cleanLockerNumber As String = If(lockerNumber, "").Trim()
            Dim cleanAssetTag As String = If(assetTag, "").Trim()
            Dim cleanActionId As String = If(actionId, "").Trim()
            Dim actionService As New LockerActionService()

            Await ClearLocalCheckoutOccupancyAfterBackendAckAsync(
        deviceType:=deviceType,
        lockerNumber:=cleanLockerNumber,
        journalId:=journalId)

            If String.IsNullOrWhiteSpace(authorizeResponse.transactionId) Then
                Throw New InvalidOperationException("Locker authorize response missing transactionId.")
            End If

            If String.IsNullOrWhiteSpace(authorizeResponse.commandId) Then
                Throw New InvalidOperationException("Locker authorize response missing commandId.")
            End If

            Dim dto As New LockerAckRequestDto With {
        .transactionId = authorizeResponse.transactionId.Trim(),
        .commandId = authorizeResponse.commandId.Trim(),
        .correlationId = cleanActionId,
        .ackStatus = "completed",
        .adapterName = AppSettings.AdapterName,
        .hardwareEventCode = "LOCKER_ASSET_CHECKOUT_COMPLETE",
        .message = $"Asset {cleanAssetTag} checked out from locker {cleanLockerNumber}.",
        .compartmentIds = New List(Of String) From {
            cleanLockerNumber
        }
    }

            Dim ackJson As String = ""

            Try
                ackJson = JsonSerializer.Serialize(dto, _jsonOpts)
            Catch
                ackJson = ""
            End Try

            If journalId.HasValue Then
                Await actionService.MarkAckPendingAsync(journalId.Value, ackJson)
            End If

            Dim bearerToken As String = GetCurrentBackendBearerToken()

            TraceLogger.Log(
        "CHECKOUT ACK START: " &
        "transactionId=" & dto.transactionId &
        "; commandId=" & dto.commandId &
        "; locker=" & cleanLockerNumber &
        "; tokenPresent=" & Not String.IsNullOrWhiteSpace(bearerToken))

            Dim ackFailureException As Exception = Nothing

            Try
                Await opsBackend.AckLockerActionAsync(
            dto,
            bearerToken:=bearerToken,
            ct:=GetWorkflowCancellationToken())
            Catch ex As Exception
                ackFailureException = ex
            End Try

            If ackFailureException IsNot Nothing Then
                TraceExceptionDeep("CHECKOUT_ACK_FAILED", ackFailureException)

                If journalId.HasValue Then
                    Await actionService.MarkAckFailedAsync(
                journalId.Value,
                ackFailureException.Message)

                    Await actionService.UpdateJournalStateAsync(
                journalId.Value,
                LockerTransactionState.NeedsReconciliation,
                LockerAckStatus.Failed,
                ackFailureException.Message)
                End If

                Return False
            End If

            TraceLogger.Log(
        "CHECKOUT ACK SUCCESS: " &
        "transactionId=" & dto.transactionId &
        "; locker=" & cleanLockerNumber)

            If journalId.HasValue Then
                Await actionService.MarkAckSucceededAsync(journalId.Value)
            End If

            Return True

        End Function


#End Region

#Region "Return / Asset deposit workflow"

        Private Sub PromptForAssetScan()
            BumpUiEpoch()

            _barcodeScanService.ResetAll()

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

            _barcodeScanService.Validator = AddressOf ValidateAssetScan

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

            If profile IsNot Nothing AndAlso
       Not String.IsNullOrWhiteSpace(profile.RejectPattern) AndAlso
       System.Text.RegularExpressions.Regex.IsMatch(value.ToUpperInvariant(), profile.RejectPattern) Then

                errorMessage = If(profile.RejectPatternMessage,
                          If(profile.RejectMessage, "Scan not recognized."))
                Return False
            End If

            If profile IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(profile.RequirePrefix) Then
                If Not value.StartsWith(profile.RequirePrefix, StringComparison.OrdinalIgnoreCase) Then
                    errorMessage = If(profile.RejectMessage,
                              $"Asset barcode must begin with {profile.RequirePrefix}.")
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
        Private Async Function SubmitAssetScanAsync(rawAssetScan As String, source As String) As Task

            Dim myEpoch As Integer = _uiEpoch
            Dim assetRaw As String = If(rawAssetScan, String.Empty).Trim()
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
                _barcodeScanService.ResetAll()
                _state = ScreenState.AwaitAssetScan
                SetUiEnabled(True)
                FocusHidSink()
                Return
            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)

            Try

                Dim workflowKey As String = If(_activeWorkflow?.WorkflowKey, "").Trim()
                Dim workflowAction As String = GetWorkflowAction(_activeWorkflow)

                TraceLogger.Log(
            "SUBMIT ASSET SCAN - validating asset. " &
            "assetTag=" & normalizedAsset &
            "; workflow=" & workflowKey &
            "; workflowAction=" & workflowAction)

                Dim opsBackend = TryCast(_backend, OperationsBackendService)

                Dim result As AssetValidateResponse

                If opsBackend IsNot Nothing Then
                    result = Await opsBackend.ValidateAssetAsync(
                assetTag:=normalizedAsset,
                workflow:=workflowKey,
                workflowAction:=workflowAction,
                ct:=GetWorkflowCancellationToken())
                Else
                    result = Await _backend.ValidateAssetAsync(
                normalizedAsset,
                GetWorkflowCancellationToken())
                End If

                If myEpoch <> _uiEpoch Then Return

                If result Is Nothing OrElse Not result.isValid Then

                    Dim msg As String = If(result?.message, String.Empty).Trim()

                    If msg.Length = 0 Then
                        msg = "This asset is not allowed for this workflow."
                    End If

                    ShowPrompt(msg & Environment.NewLine & Environment.NewLine &
                       "Please scan the correct asset barcode, or cancel to start over.")

                    _activeAssetTag = Nothing
                    _activeDeviceType = Nothing
                    _activeSizeCode = Nothing

                    _barcodeScanService.ResetAll()
                    KeypadControl.Reset()

                    _state = ScreenState.AwaitAssetScan
                    SetUiEnabled(True)
                    FocusHidSink()
                    Return

                End If

                _activeAssetTag = If(result.assetTag, normalizedAsset).Trim()
                _activeDeviceType = If(result.deviceType, "").Trim()
                _activeSizeCode = If(result.sizeCode, "").Trim()

                TraceLogger.Log(
            "SUBMIT ASSET SCAN - validation accepted. " &
            "assetTag=" & _activeAssetTag &
            "; deviceType=" & _activeDeviceType &
            "; sizeCode=" & _activeSizeCode &
            "; workflow=" & workflowKey &
            "; workflowAction=" & workflowAction)

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.User,
            .ActorId = If(_courierAuth IsNot Nothing, $"User:{_courierAuth.UserId}", "User:Unknown"),
            .AffectedComponent = "LockerAccessWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = Guid.NewGuid().ToString("N"),
            .ReasonCode = $"AssetScanned;AssetTag={normalizedAsset};Source={source};Workflow={GetActiveWorkflowKey()};WorkflowAction={workflowAction}"
        })

                If myEpoch <> _uiEpoch Then Return

                AdvanceToNextStep()
                Return

            Catch ex As Exception

                If myEpoch <> _uiEpoch Then Return

                TraceExceptionDeep("SUBMIT_ASSET_SCAN_FAILED", ex)

                ShowPrompt("System unavailable. Please try again.")

                _state = ScreenState.AwaitAssetScan
                _barcodeScanService.ResetAll()
                KeypadControl.Reset()
                SetUiEnabled(True)
                FocusHidSink()
                Return

            Finally

                If myEpoch = _uiEpoch Then
                    If Not CompleteDeferredReturnToStartIfRequested() Then
                        SetUiEnabled(True)
                        FocusHidSink()
                    End If
                End If

            End Try

        End Function
        Private Function ValidateAssetScan(scan As String) As ScanValidationResult
            Dim v As String = If(scan, "").Trim().ToUpperInvariant()

            If v.Length = 0 Then
                Return ScanValidationResult.Invalid("Asset barcode is empty.")
            End If

            ' Reject MAC-style labels: 12 hex chars
            If System.Text.RegularExpressions.Regex.IsMatch(v, "^[0-9A-F]{12}$") Then
                Return ScanValidationResult.Invalid("Scan serial number (S/N), not MAC label.")
            End If

            ' Reject duplicated MAC-style scan: 12 hex chars repeated twice
            If System.Text.RegularExpressions.Regex.IsMatch(v, "^([0-9A-F]{12})\1$") Then
                Return ScanValidationResult.Invalid("Scan serial number (S/N), not MAC label.")
            End If

            Return ScanValidationResult.Valid()
        End Function
        Private Async Sub PromptForDefectDecision()

            Dim myEpoch As Integer = _uiEpoch

            Try
                TraceLogger.Log($"DEFECT DECISION START epoch={myEpoch}, currentEpoch={_uiEpoch}")

                _state = ScreenState.AwaitDefectDecision

                ' IMPORTANT:
                ' Do NOT call SetUiEnabled(False) here.
                ' If SetUiEnabled(False) disables a parent container, the defect buttons
                ' may be created but not visible/clickable.

                Dim options As New List(Of DefectOption) From {
            New DefectOption("Normal Return", "NORMAL"),
            New DefectOption("Defective", "DEFECTIVE")
        }

                TraceLogger.Log($"DEFECT OPTIONS COUNT={options.Count}")

                Dim selected = Await ShowTouchSelectionAsync(
            message:="Is this device defective?",
            options:=options,
            timeoutSeconds:=20,
            myEpoch:=myEpoch)

                TraceLogger.Log($"DEFECT DECISION RESULT selected='{If(selected, "<NULL>")}', epoch={myEpoch}, currentEpoch={_uiEpoch}")

                If myEpoch <> _uiEpoch Then
                    TraceLogger.Log("DEFECT DECISION ABORTED - epoch changed after selection.")
                    Return
                End If

                If String.IsNullOrWhiteSpace(selected) Then
                    TraceLogger.Log("DEFECT DECISION TIMED OUT - returning to start.")

                    ShowPrompt("Selection timed out. Returning to start.")
                    Await Task.Delay(3000)

                    If myEpoch = _uiEpoch Then
                        ResetToAwaitWorkflowChoice()
                    End If

                    Return
                End If

                If String.Equals(selected, "DEFECTIVE", StringComparison.OrdinalIgnoreCase) Then
                    TraceLogger.Log("DEFECT DECISION SELECTED: DEFECTIVE")

                    _isDefectiveReturn = True
                    PromptForDefectType()
                Else
                    TraceLogger.Log("DEFECT DECISION SELECTED: NORMAL")

                    _isDefectiveReturn = False
                    _selectedDefectType = Nothing
                    AdvanceToNextStep()
                End If
            Catch ex As Exception

                TraceLogger.Log("DEFECT DECISION ERROR: " & ex.ToString())

                ShowPrompt("An error occurred while asking for the defect status. Returning to start.")

                ResetToAwaitWorkflowChoice()

            End Try

        End Sub
        Private Async Sub PromptForDefectType()

            Dim myEpoch As Integer = _uiEpoch

            _state = ScreenState.AwaitDefectType

            Dim options As New List(Of DefectOption) From {
        New DefectOption("Battery Issue", "Battery Issue"),
        New DefectOption("Screen Damage", "Screen Damage"),
        New DefectOption("Won't Power On", "Won't Power On"),
        New DefectOption("Connectivity Issue", "Connectivity Issue"),
        New DefectOption("Physical Damage", "Physical Damage"),
        New DefectOption("Missing Parts", "Missing Parts"),
        New DefectOption("Other", "Other")
    }

            Dim selected = Await ShowTouchSelectionAsync(
        message:="Select defect type:",
        options:=options,
        timeoutSeconds:=30,
        myEpoch:=myEpoch)

            If myEpoch <> _uiEpoch Then Return

            If String.IsNullOrWhiteSpace(selected) Then
                _selectedDefectType = "Unspecified"
            Else
                _selectedDefectType = selected
            End If

            AdvanceToNextStep()

        End Sub
        Private Class DefectOption
            Public Property Text As String
            Public Property Value As String

            Public Sub New(text As String, value As String)
                Me.Text = text
                Me.Value = value
            End Sub
        End Class
        Private Async Function ShowTouchSelectionAsync(
    message As String,
    options As List(Of DefectOption),
    timeoutSeconds As Integer,
    myEpoch As Integer
) As Task(Of String)

            Try
                TraceLogger.Log($"SHOW TOUCH SELECTION START message='{message}', timeout={timeoutSeconds}, epoch={myEpoch}, currentEpoch={_uiEpoch}")

                If options Is Nothing Then
                    TraceLogger.Log("SHOW TOUCH SELECTION ABORT - options is Nothing.")
                    Return Nothing
                End If

                If options.Count = 0 Then
                    TraceLogger.Log("SHOW TOUCH SELECTION ABORT - options.Count = 0.")
                    Return Nothing
                End If

                TraceLogger.Log($"SHOW TOUCH SELECTION OPTIONS COUNT={options.Count}")

                Dim tcs As New TaskCompletionSource(Of String)(
            TaskCreationOptions.RunContinuationsAsynchronously)
                _touchSelectionTcs = tcs

                Await Dispatcher.InvokeAsync(
            Sub()
                TraceLogger.Log("SHOW TOUCH SELECTION UI BUILD START")

                DefectButtonGrid.Children.Clear()
                DefectPromptText.Text = message

                DefectPanel.Visibility = Visibility.Visible
                DefectPanel.IsEnabled = True
                DefectPanel.IsHitTestVisible = True

                DefectButtonGrid.Visibility = Visibility.Visible
                DefectButtonGrid.IsEnabled = True
                DefectButtonGrid.IsHitTestVisible = True

                TraceLogger.Log($"DEFECT PANEL Visibility={DefectPanel.Visibility}, IsEnabled={DefectPanel.IsEnabled}, IsHitTestVisible={DefectPanel.IsHitTestVisible}")
                TraceLogger.Log($"DEFECT GRID Visibility={DefectButtonGrid.Visibility}, IsEnabled={DefectButtonGrid.IsEnabled}, IsHitTestVisible={DefectButtonGrid.IsHitTestVisible}")
                TraceLogger.Log($"DEFECT GRID CHILDREN BEFORE={DefectButtonGrid.Children.Count}")

                For Each opt In options

                    TraceLogger.Log($"ADDING DEFECT BUTTON Text='{opt.Text}', Value='{opt.Value}'")

                    Dim btn As New Button With {
        .Content = opt.Text,
        .Tag = opt.Value,
        .FontSize = 30,
        .FontWeight = FontWeights.Bold,
        .MinHeight = 95,
        .Margin = New Thickness(10),
        .Padding = New Thickness(20),
        .IsEnabled = True,
        .IsHitTestVisible = True,
        .Focusable = False,
        .HorizontalAlignment = HorizontalAlignment.Stretch,
        .VerticalAlignment = VerticalAlignment.Stretch,
        .Background = TryCast(Application.Current.Resources("BrandPrimaryButtonBrush"), Brush),
        .Foreground = TryCast(Application.Current.Resources("BrandPrimaryButtonTextBrush"), Brush)
    }

                    AddHandler btn.Click,
        Sub(sender, e)

            e.Handled = True

            TraceLogger.Log($"DEFECT BUTTON CLICK received. epoch={myEpoch}, currentEpoch={_uiEpoch}")

            HandleDefectSelection(sender, tcs, myEpoch)

        End Sub

                    AddHandler btn.PreviewMouseLeftButtonDown,
        Sub(sender, e)

            e.Handled = True

            TraceLogger.Log($"DEFECT BUTTON MOUSE DOWN received. epoch={myEpoch}, currentEpoch={_uiEpoch}")

            HandleDefectSelection(sender, tcs, myEpoch)

        End Sub

                    AddHandler btn.PreviewTouchDown,
        Sub(sender, e)

            e.Handled = True

            TraceLogger.Log($"DEFECT BUTTON TOUCH DOWN received. epoch={myEpoch}, currentEpoch={_uiEpoch}")

            HandleDefectSelection(sender, tcs, myEpoch)

        End Sub

                    DefectButtonGrid.Children.Add(btn)

                Next

                TraceLogger.Log($"DEFECT GRID CHILDREN AFTER={DefectButtonGrid.Children.Count}")
                TraceLogger.Log("SHOW TOUCH SELECTION UI BUILD END")
            End Sub)

                Dim timeoutTask As Task = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))
                Dim completedTask As Task = Await Task.WhenAny(tcs.Task, timeoutTask)

                If completedTask Is timeoutTask Then
                    TraceLogger.Log("SHOW TOUCH SELECTION TIMEOUT reached.")
                Else
                    TraceLogger.Log("SHOW TOUCH SELECTION completed by button click.")
                End If

                Await Dispatcher.InvokeAsync(
            Sub()
                TraceLogger.Log($"SHOW TOUCH SELECTION CLEANUP START - GridChildren={DefectButtonGrid.Children.Count}")

                DefectPanel.Visibility = Visibility.Collapsed
                DefectButtonGrid.Children.Clear()
                If ReferenceEquals(_touchSelectionTcs, tcs) Then
                    _touchSelectionTcs = Nothing
                End If

                TraceLogger.Log($"SHOW TOUCH SELECTION CLEANUP END - PanelVisibility={DefectPanel.Visibility}, GridChildren={DefectButtonGrid.Children.Count}")
            End Sub)

                If myEpoch <> _uiEpoch Then
                    TraceLogger.Log("SHOW TOUCH SELECTION returning Nothing - epoch changed after completion.")
                    Return Nothing
                End If

                If completedTask Is timeoutTask Then
                    Return Nothing
                End If

                Dim result As String = Await tcs.Task
                TraceLogger.Log($"SHOW TOUCH SELECTION RETURN='{If(result, "<NULL>")}'")

                Return result

            Catch ex As Exception
                TraceLogger.Log($"SHOW TOUCH SELECTION ERROR: {ex}")
                Return Nothing
            End Try

        End Function
        Private Async Sub ProcessAssetDepositAssignmentAsync()

            Dim myEpoch As Integer = _uiEpoch
            Dim actionId As String = Guid.NewGuid().ToString("N")
            Dim openResult As LockerActionResult = Nothing
            Dim processException As Exception = Nothing
            Dim authorizeResponse As LockerAuthorizeResponseDto = Nothing

            Dim lockerNumber As String = Nothing
            Dim shouldEndSession As Boolean = False
            Dim delayBeforeEndMs As Integer = 1500

            TraceLogger.Log(
        $"ASSET DEPOSIT START actionId={actionId}; " &
        $"epoch={myEpoch}; " &
        $"assetTag={_activeAssetTag}; " &
        $"workflow={GetActiveWorkflowKey()}; " &
        $"workflowAction={GetWorkflowAction(_activeWorkflow)}")

            If String.IsNullOrWhiteSpace(_activeAssetTag) Then

                TraceLogger.Log("ASSET DEPOSIT ABORT missing active asset tag")

                ShowPrompt("Scan an asset tag first.")
                SetActiveStep("asset_scan")
                Return

            End If

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            SizeSelectionPanel.Visibility = Visibility.Collapsed

            Try

                Dim actorId As String = GetAuthorizedActorIdForCurrentSession()

                If String.IsNullOrWhiteSpace(actorId) Then
                    Throw New InvalidOperationException("ActorId is required before asset deposit assignment.")
                End If

                TraceLogger.Log(
            "ASSET DEPOSIT actor resolved. " &
            "actorId=" & actorId &
            "; courierAuthPresent=" & (_courierAuth IsNot Nothing).ToString() &
            "; authResultPresent=" & (_authResult IsNot Nothing).ToString())

                ShowPrompt("Finding available compartment.")

                TraceLogger.Log("ASSET DEPOSIT releasing expired reservations")
                ReleaseExpiredReservations()

                TraceLogger.Log("ASSET DEPOSIT selecting available locker")
                lockerNumber = _assigner.SelectNextAvailableAssetLockerNumber()

                TraceLogger.Log($"ASSET DEPOSIT selected locker='{lockerNumber}'")

                If String.IsNullOrWhiteSpace(lockerNumber) Then

                    TraceLogger.Log("ASSET DEPOSIT no compartment available")

                    ShowPrompt("No compartments are currently available.")
                    shouldEndSession = True
                    delayBeforeEndMs = 3000

                Else

                    ShowPrompt($"Reserving compartment {lockerNumber}.")

                    TraceLogger.Log(
                $"ASSET DEPOSIT reserving locker={lockerNumber}; " &
                $"asset={_activeAssetTag}; " &
                $"actionId={actionId}; " &
                $"actorId={actorId}")

                    ReserveLockerForDelivery(
                lockerNumber:=lockerNumber,
                workOrderNumber:=_activeAssetTag,
                correlationId:=actionId)

                    ShowPrompt($"Authorizing compartment {lockerNumber}.")

                    TraceLogger.Log(
                $"ASSET DEPOSIT authorizing backend locker action; " &
                $"locker={lockerNumber}; " &
                $"asset={_activeAssetTag}; " &
                $"localActionId={actionId}; " &
                $"actorId={actorId}")

                    authorizeResponse =
                Await AuthorizeLockerOpenActionForAssetAsync(
                    assetTag:=_activeAssetTag,
                    lockerNumber:=lockerNumber,
                    correlationId:=actionId,
                    sessionUserId:=actorId,
                    ct:=GetWorkflowCancellationToken())

                    If myEpoch <> _uiEpoch Then
                        TraceLogger.Log("ASSET DEPOSIT ABORT epoch changed after locker authorize")
                        Return
                    End If

                    If authorizeResponse Is Nothing Then

                        ReleaseLockerReservation(lockerNumber, "AssetLockerAuthorizeEmptyResponse")

                        Throw New InvalidOperationException(
                    "Backend locker authorization returned an empty response.")

                    End If

                    If authorizeResponse.authorization IsNot Nothing AndAlso
               Not authorizeResponse.authorization.isAuthorized Then

                        TraceLogger.Log(
                    $"ASSET DEPOSIT backend locker authorization denied; " &
                    $"locker={lockerNumber}; " &
                    $"asset={_activeAssetTag}; " &
                    $"localActionId={actionId}; " &
                    $"actorId={actorId}")

                        ReleaseLockerReservation(lockerNumber, "AssetLockerAuthorizeDenied")

                        ShowPrompt($"Asset deposit was not authorized for compartment {lockerNumber}.")
                        shouldEndSession = True
                        delayBeforeEndMs = 3000

                    End If

                    If Not shouldEndSession Then
                    If String.IsNullOrWhiteSpace(authorizeResponse.transactionId) OrElse
               String.IsNullOrWhiteSpace(authorizeResponse.commandId) Then

                        ReleaseLockerReservation(lockerNumber, "AssetLockerAuthorizeMissingTransaction")

                        Throw New InvalidOperationException(
                    "Backend locker authorization did not return the required transactionId/commandId.")

                    End If

                    TraceLogger.Log(
                $"ASSET DEPOSIT backend locker action authorized; " &
                $"locker={lockerNumber}; " &
                $"asset={_activeAssetTag}; " &
                $"localActionId={actionId}; " &
                $"actorId={actorId}; " &
                $"backendTransactionId={authorizeResponse.transactionId}; " &
                $"backendCommandId={authorizeResponse.commandId}")

                    ShowPrompt($"Opening compartment {lockerNumber}.")

                    TraceLogger.Log(
                $"ASSET DEPOSIT opening locker={lockerNumber}; " &
                $"asset={_activeAssetTag}; " &
                $"actionId={actionId}")

                    openResult =
                Await TryOpenLockerWithJournalAsync(
                    actionId,
                    _activeAssetTag,
                    lockerNumber)

                    Dim opened As Boolean =
                openResult IsNot Nothing AndAlso openResult.Success

                    TraceLogger.Log($"ASSET DEPOSIT open result={opened}; locker={lockerNumber}")

                    If Not opened Then

                        TraceLogger.Log($"ASSET DEPOSIT open failed; releasing reservation locker={lockerNumber}")

                        ReleaseLockerReservation(lockerNumber, "AssetOpenFailed")
                        ShowPrompt($"Compartment {lockerNumber} could not be opened.")
                        shouldEndSession = True
                        delayBeforeEndMs = 3000

                    Else

                        ShowPrompt($"Compartment {lockerNumber} opened. Place asset inside and close the door.")

                        TraceLogger.Log($"ASSET DEPOSIT waiting for door close locker={lockerNumber}")

                        Dim closedOk As Boolean =
                    Await WaitForLockerClosedAsync(
                        lockerNumber:=lockerNumber,
                        timeoutMs:=120000,
                        myEpoch:=myEpoch)

                        TraceLogger.Log(
                    $"ASSET DEPOSIT door close result={closedOk}; " &
                    $"locker={lockerNumber}; currentEpoch={_uiEpoch}; myEpoch={myEpoch}")

                        If myEpoch <> _uiEpoch Then

                            TraceLogger.Log("ASSET DEPOSIT ABORT epoch changed after door close wait")
                            Return

                        End If

                        If Not closedOk Then

                            TraceLogger.Log($"ASSET DEPOSIT door close timeout locker={lockerNumber}")

                            ShowPrompt($"Please close compartment {lockerNumber} to continue.")
                            shouldEndSession = True
                            delayBeforeEndMs = 3000

                        Else

                            TraceLogger.Log(
                        $"ASSET DEPOSIT completing asset deposit asset={_activeAssetTag}; " &
                        $"locker={lockerNumber}; localActionId={actionId}; " &
                        $"actorId={actorId}; " &
                        $"backendTransactionId={authorizeResponse.transactionId}")

                            Await CompleteAssetDepositAsync(
                        assetTag:=_activeAssetTag,
                        lockerNumber:=lockerNumber,
                        actionId:=actionId,
                        authorizeResponse:=authorizeResponse,
                        journalId:=If(openResult Is Nothing,
                                      CType(Nothing, Integer?),
                                      openResult.JournalId))

                            TraceLogger.Log(
                        $"ASSET DEPOSIT CompleteAssetDepositAsync returned asset={_activeAssetTag}; " &
                        $"locker={lockerNumber}; actionId={actionId}")

                            ShowPrompt("Asset deposit complete. Thank you.")

                            shouldEndSession = True
                            delayBeforeEndMs = 1500

                        End If

                    End If
                    End If

                End If

            Catch ex As Exception

                processException = ex

            End Try

            If processException IsNot Nothing Then

                If myEpoch <> _uiEpoch Then

                    TraceLogger.LogExceptionDeep(
                "ASSET_DEPOSIT_EXCEPTION_AFTER_EPOCH_CHANGE",
                processException)

                    Return

                End If

                TraceLogger.LogExceptionDeep("ASSET_DEPOSIT_FAIL", processException)

                If Not String.IsNullOrWhiteSpace(lockerNumber) Then

                    Try
                        ReleaseLockerReservation(lockerNumber, "AssetDepositException")
                    Catch releaseEx As Exception
                        TraceLogger.LogExceptionDeep(
                    "ASSET_DEPOSIT_RELEASE_RESERVATION_FAILED",
                    releaseEx)
                    End Try

                End If

                If openResult IsNot Nothing Then

                    Try
                        Await New LockerActionService().
                    UpdateJournalStateAsync(
                        openResult.JournalId,
                        LockerTransactionState.NeedsReconciliation,
                        LockerAckStatus.Failed,
                        processException.Message)

                    Catch updateEx As Exception
                        TraceLogger.LogExceptionDeep(
                    "ASSET_DEPOSIT_JOURNAL_UPDATE_FAILED",
                    updateEx)
                    End Try

                End If

                ShowPrompt($"System unavailable: {processException.Message}")
                shouldEndSession = True
                delayBeforeEndMs = 3000

            End If

            If myEpoch = _uiEpoch Then

                TraceLogger.Log("ASSET DEPOSIT restoring UI enabled/focus")
                SetUiEnabled(True)
                FocusHidSink()

            Else

                TraceLogger.Log(
            $"ASSET DEPOSIT skipped UI restore; epoch changed current={_uiEpoch}; mine={myEpoch}")

            End If

            If myEpoch = _uiEpoch AndAlso shouldEndSession Then

                TraceLogger.Log($"ASSET DEPOSIT ending session after delay={delayBeforeEndMs}ms")

                Await Task.Delay(delayBeforeEndMs)

                If myEpoch = _uiEpoch Then
                    ResetToAwaitWorkflowChoice()
                End If

            End If

        End Sub
        Private Sub ReleaseExpiredReservations()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim nowUtc = DateTime.UtcNow

                Dim expired = db.LockerStatuses.
            Where(Function(s) s.OccupancyState = OccupancyState.Reserved AndAlso
                              s.ReservedUntilUtc.HasValue AndAlso
                              s.ReservedUntilUtc.Value < nowUtc).
            ToList()

                For Each s In expired
                    s.OccupancyState = OccupancyState.Vacant
                    s.PackagePresent = False

                    s.ReservedUntilUtc = Nothing
                    s.ReservedCorrelationId = ""
                    s.ReservedWorkOrderNumber = ""

                    s.LastReason = "ExpiredReservationReleased"
                    s.LastUpdatedUtc = nowUtc
                Next

                If expired.Count > 0 Then
                    Try
                        db.SaveChanges()

                    Catch ex As Exception
                        TraceExceptionDeep("RELEASE_EXPIRED_RESERVATIONS_SAVE_FAILED", ex)
                        Throw
                    End Try
                End If

            End Using

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

            If _authResult Is Nothing Then
                TraceLogger.Log("CHECKOUT: _authResult is Nothing.")
                Return Nothing
            End If

            If _authResult.AuthorizedDevices Is Nothing OrElse _authResult.AuthorizedDevices.Count = 0 Then
                TraceLogger.Log("CHECKOUT: No AuthorizedDevices found on AuthResult.")
                Return Nothing
            End If

            Dim authorizedTypes =
        _authResult.AuthorizedDevices.
            Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
            Select(Function(x) x.Trim().ToUpperInvariant()).
            Distinct().
            ToList()

            For Each deviceType In authorizedTypes
                TraceLogger.Log("CHECKOUT: Testing authorized device type " & deviceType)

                Dim lockerNumber = _assigner.SelectNextOccupiedLockerNumberByDeviceType(deviceType)

                If Not String.IsNullOrWhiteSpace(lockerNumber) Then
                    TraceLogger.Log("CHECKOUT: Selected device type " & deviceType & " from locker " & lockerNumber)
                    Return deviceType
                End If
            Next

            TraceLogger.Log("CHECKOUT: Authorized devices found, but no occupied locker matched.")
            Return Nothing

        End Function
        Private Sub SelectDefectOption(
    sender As Object,
    tcs As TaskCompletionSource(Of String),
    myEpoch As Integer)

            TraceLogger.Log($"DEFECT OPTION INPUT received. epoch={myEpoch}, currentEpoch={_uiEpoch}")

            If myEpoch <> _uiEpoch Then
                tcs.TrySetResult(Nothing)
                Return
            End If

            Dim selectedButton = TryCast(sender, Button)

            If selectedButton Is Nothing OrElse selectedButton.Tag Is Nothing Then
                TraceLogger.Log("DEFECT OPTION INPUT failed - sender/tag missing.")
                tcs.TrySetResult(Nothing)
                Return
            End If

            Dim value As String = CStr(selectedButton.Tag)

            TraceLogger.Log("DEFECT OPTION SELECTED: " & value)

            tcs.TrySetResult(value)

        End Sub
        Private Sub HandleDefectSelection(
    sender As Object,
    tcs As TaskCompletionSource(Of String),
    myEpoch As Integer)

            Try

                If myEpoch <> _uiEpoch Then

                    TraceLogger.Log("DEFECT SELECTION ignored - epoch changed.")

                    tcs.TrySetResult(Nothing)
                    Return

                End If

                Dim selectedButton = TryCast(sender, Button)

                If selectedButton Is Nothing Then

                    TraceLogger.Log("DEFECT SELECTION failed - sender not button.")

                    tcs.TrySetResult(Nothing)
                    Return

                End If

                If selectedButton.Tag Is Nothing Then

                    TraceLogger.Log("DEFECT SELECTION failed - button tag missing.")

                    tcs.TrySetResult(Nothing)
                    Return

                End If

                Dim value As String = CStr(selectedButton.Tag)

                TraceLogger.Log($"DEFECT SELECTION ACCEPTED value='{value}'")

                tcs.TrySetResult(value)

            Catch ex As Exception

                TraceLogger.Log("HANDLE DEFECT SELECTION ERROR: " & ex.ToString())

                tcs.TrySetResult(Nothing)

            End Try

        End Sub
        Private Function GetAuthorizedActorIdForCurrentSession() As String

            Dim actorId As String = ""

            If _courierAuth IsNot Nothing Then

                actorId = If(_courierAuth.ActorID, "").Trim()

                If String.IsNullOrWhiteSpace(actorId) Then
                    actorId = If(_courierAuth.UserId, "").Trim()
                End If

            End If

            If String.IsNullOrWhiteSpace(actorId) AndAlso _authResult IsNot Nothing Then

                actorId = If(_authResult.ActorID, "").Trim()

                If String.IsNullOrWhiteSpace(actorId) Then
                    actorId = If(_authResult.UserId, "").Trim()
                End If

            End If

            Return If(actorId, "").Trim()

        End Function
#End Region

#Region "Shared locker interaction helpers"
        Private Async Function TryOpenLockerWithJournalAsync(actionId As String,
                                                     workOrderNumber As String,
                                                     lockerNumber As String) As Task(Of LockerActionResult)

            Dim safeLockerNumber As String = If(lockerNumber, "").Trim()
            Dim safeWorkOrderNumber As String = If(workOrderNumber, "").Trim()
            Dim workflowReference As String =
    GetCurrentWorkflowReference(safeWorkOrderNumber)
            Dim actorId As String = If(_authResult IsNot Nothing,
                               $"User:{_authResult.UserId}",
                               "User:Unknown")

            If String.IsNullOrWhiteSpace(safeLockerNumber) Then
                Throw New ArgumentException("Locker number is required.", NameOf(lockerNumber))
            End If

            Dim locker As Locker = Nothing

            Using db = DatabaseBootstrapper.BuildDbContext()

                locker = db.Lockers.
            AsNoTracking().
            SingleOrDefault(Function(l) l.LockerNumber = safeLockerNumber)

            End Using

            If locker Is Nothing Then
                Throw New InvalidOperationException($"Locker {safeLockerNumber} was not found.")
            End If

            Dim actionService As New LockerActionService()

            Dim request As New LockerActionRequest With {
        .Workflow = If(String.IsNullOrWhiteSpace(GetActiveWorkflowKey()),
                       "workflow",
                       GetActiveWorkflowKey()),
        .ActionType = "WorkflowOpenLocker",
        .LockerId = locker.LockerId,
        .LockerNumber = locker.LockerNumber,
        .Branch = locker.Branch,
        .RelayId = locker.RelayId,
        .ActorId = actorId,
        .Credential = If(_courierAuth?.UserId, _authResult?.UserId),
        .AssetTag = workflowReference,
.DeviceType = _activeDeviceType,
.CorrelationId = actionId,
.TransactionId = workflowReference,
        .RequiresBackendAck = True
    }

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.LockerOpenAttempt,
        .ActorType = Audit.ActorType.User,
        .ActorId = actorId,
        .AffectedComponent = "LockerControllerService",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = actionId,
        .ReasonCode = $"WorkflowOpenRequested;Workflow={request.Workflow};Ref={safeWorkOrderNumber};Locker={safeLockerNumber}"
    })

            Dim result = Await actionService.ExecuteLockerActionAsync(
        request,
        Function()

            If _lockerController Is Nothing Then
                Throw New InvalidOperationException("Locker controller service is not available.")
            End If

            Dim opened = _lockerController.UnlockByLockerNumber(safeLockerNumber)

            If Not opened Then
                Throw New InvalidOperationException(
                    $"Unlock command was not accepted for locker {safeLockerNumber}.")
            End If

            Return Task.CompletedTask

        End Function)

            If Not result.Success Then
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.LockerOpenAttempt,
            .ActorType = Audit.ActorType.User,
            .ActorId = actorId,
            .AffectedComponent = "LockerControllerService",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = $"WorkflowOpenFailed;Workflow={request.Workflow};Ref={safeWorkOrderNumber};Locker={safeLockerNumber};Error={result.ErrorMessage}"
        })

                ShowPrompt($"Unable to open locker {safeLockerNumber}. Please contact attendant.")
            End If

            Return result

        End Function
        Private Async Function WaitForLockerClosedAsync(lockerNumber As String,
                                                timeoutMs As Integer,
                                                myEpoch As Integer) As Task(Of Boolean)

            Dim startedUtc = DateTime.UtcNow
            _lockerDoorCycleActive = True
            _lastDoorCycleReturnRequestUtc = DateTime.MinValue

            Try
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
            Finally
                _lockerDoorCycleActive = False
                _lastDoorCycleReturnRequestUtc = DateTime.MinValue
            End Try

        End Function
        Private Async Function CompleteAssetDepositAsync(
    assetTag As String,
    lockerNumber As String,
    actionId As String,
    authorizeResponse As LockerAuthorizeResponseDto,
    Optional journalId As Integer? = Nothing
) As Task

            Dim cleanAssetTag As String = If(assetTag, "").Trim()
            Dim cleanLockerNumber As String = If(lockerNumber, "").Trim()
            Dim cleanActionId As String = If(actionId, "").Trim()

            If String.IsNullOrWhiteSpace(cleanAssetTag) Then
                Throw New InvalidOperationException("Asset tag is missing.")
            End If

            If String.IsNullOrWhiteSpace(cleanLockerNumber) Then
                Throw New InvalidOperationException("Locker number is missing.")
            End If

            If String.IsNullOrWhiteSpace(cleanActionId) Then
                cleanActionId = Guid.NewGuid().ToString("N")
            End If

            If authorizeResponse Is Nothing Then
                Throw New InvalidOperationException("Locker authorization response is missing.")
            End If

            If String.IsNullOrWhiteSpace(authorizeResponse.transactionId) Then
                Throw New InvalidOperationException("Backend locker authorization did not return transactionId.")
            End If

            If String.IsNullOrWhiteSpace(authorizeResponse.commandId) Then
                Throw New InvalidOperationException("Backend locker authorization did not return commandId.")
            End If

            Dim actorId As String = ""

            If _courierAuth IsNot Nothing Then
                actorId = If(_courierAuth.ActorID, _courierAuth.UserId)
            End If

            If String.IsNullOrWhiteSpace(actorId) AndAlso _authResult IsNot Nothing Then
                actorId = If(_authResult.ActorID, _authResult.UserId)
            End If

            actorId = If(actorId, "").Trim()

            Dim deviceType As String = If(_activeDeviceType, "").Trim()

            If String.IsNullOrWhiteSpace(deviceType) Then
                deviceType = ResolveDeviceTypeFromAsset(cleanAssetTag)
            End If

            If String.IsNullOrWhiteSpace(deviceType) Then
                deviceType = "UNKNOWN"
            End If

            Dim defectType As String = ""

            If _isDefectiveReturn Then
                defectType = If(String.IsNullOrWhiteSpace(_selectedDefectType),
                        "Unspecified",
                        _selectedDefectType.Trim())
            End If

            TraceLogger.Log($"ASSET COMPLETE local DB update start asset={cleanAssetTag}; locker={cleanLockerNumber}; deviceType={deviceType}; actor={actorId}")

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = cleanLockerNumber)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {cleanLockerNumber} was not found.")
                End If

                If locker.Status Is Nothing Then
                    Throw New InvalidOperationException($"Locker {cleanLockerNumber} does not have a LockerStatus row.")
                End If

                locker.Status.OccupancyState = OccupancyState.Occupied
                locker.Status.PackagePresent = True
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "AssetDeposited"
                locker.Status.LastActorId = actorId

                locker.Status.CurrentAssetTag = cleanAssetTag
                locker.Status.CurrentDeviceType = deviceType
                locker.Status.IsDefectiveHold = _isDefectiveReturn
                locker.Status.DefectType = defectType

                locker.Status.ReservedUntilUtc = Nothing
                locker.Status.ReservedCorrelationId = ""
                locker.Status.ReservedWorkOrderNumber = ""

                Try
                    db.SaveChanges()
                Catch ex As Exception
                    TraceExceptionDeep("ASSET_COMPLETE_DB_SAVE_FAILED", ex)
                    Throw
                End Try

            End Using

            TraceLogger.Log($"ASSET COMPLETE local DB update saved asset={cleanAssetTag}; locker={cleanLockerNumber}")

            Dim actionService As New LockerActionService()

            If journalId.HasValue Then
                Await actionService.MarkDoorClosedAndLocalStateUpdatedAsync(journalId.Value)
            End If

            Dim ack As New LockerAckRequestDto With {
        .transactionId = authorizeResponse.transactionId.Trim(),
        .commandId = authorizeResponse.commandId.Trim(),
        .correlationId = cleanActionId,
        .ackStatus = "completed",
        .adapterName = AppSettings.AdapterName,
        .hardwareEventCode = "LOCKER_ASSET_DEPOSIT_COMPLETE",
        .message = $"Asset {cleanAssetTag} deposited in locker {cleanLockerNumber}.",
        .compartmentIds = New List(Of String) From {
            cleanLockerNumber
        }
    }

            Dim ackJson As String = ""

            Try
                ackJson = JsonSerializer.Serialize(ack, _jsonOpts)
            Catch
                ackJson = ""
            End Try

            If journalId.HasValue Then
                Await actionService.MarkAckPendingAsync(journalId.Value, ackJson)
            End If

            Dim bearerToken As String = ""

            If _courierAuth IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_courierAuth.SessionToken) Then
                bearerToken = _courierAuth.SessionToken.Trim()
            ElseIf _authResult IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_authResult.SessionToken) Then
                bearerToken = _authResult.SessionToken.Trim()
            End If

            Dim ackFailureException As Exception = Nothing

            Try

                TraceLogger.Log(
            $"ASSET COMPLETE sending locker ACK; " &
            $"tokenPresent={Not String.IsNullOrWhiteSpace(bearerToken)}; " &
            $"localActionId={cleanActionId}; " &
            $"backendTransactionId={ack.transactionId}; " &
            $"backendCommandId={ack.commandId}")

                Await _backend.AckLockerActionAsync(
            dto:=ack,
            bearerToken:=bearerToken,
            ct:=GetWorkflowCancellationToken())

                TraceLogger.Log(
            $"ASSET COMPLETE locker ACK succeeded; " &
            $"localActionId={cleanActionId}; " &
            $"backendTransactionId={ack.transactionId}")

                If journalId.HasValue Then
                    Await actionService.MarkAckSucceededAsync(journalId.Value)
                End If

            Catch ex As Exception
                ackFailureException = ex
            End Try

            If ackFailureException IsNot Nothing Then

                TraceExceptionDeep("ASSET_COMPLETE_ACK_FAILED_NONFATAL", ackFailureException)

                TraceLogger.Log(
            $"ASSET COMPLETE local deposit remains complete but backend ACK failed; " &
            $"asset={cleanAssetTag}; locker={cleanLockerNumber}; " &
            $"localActionId={cleanActionId}; " &
            $"backendTransactionId={ack.transactionId}; " &
            $"error={ackFailureException.Message}")

                If journalId.HasValue Then

                    Await actionService.MarkAckFailedAsync(
                journalId.Value,
                ackFailureException.Message)

                    Await actionService.UpdateJournalStateAsync(
                journalId.Value,
                LockerTransactionState.NeedsReconciliation,
                LockerAckStatus.Failed,
                ackFailureException.Message)

                End If

                Return

            End If

        End Function
        Private Async Function CompletePickupForLockersAsync(workOrderNumber As String,
                                                     lockerNumbers As List(Of String),
                                                     openResults As List(Of LockerActionResult)) As Task

            Dim wo As String = If(workOrderNumber, "").Trim()

            If wo.Length = 0 Then Return
            If lockerNumbers Is Nothing OrElse lockerNumbers.Count = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim lockers = db.Lockers.
            Include(Function(l) l.Status).
            Where(Function(l) lockerNumbers.Contains(l.LockerNumber)).
            ToList()

                For Each locker In lockers

                    If locker Is Nothing OrElse locker.Status Is Nothing Then Continue For

                    locker.Status.OccupancyState = OccupancyState.Vacant
                    locker.Status.PackagePresent = False
                    locker.Status.LastUpdatedUtc = DateTime.UtcNow
                    locker.Status.LastReason = "PickupCompleted"
                    locker.Status.LastActorId = CurrentActorId(ActorType.User)

                    locker.Status.LastWorkOrderNumber = wo
                    locker.Status.ReservedWorkOrderNumber = ""
                    locker.Status.ReservedCorrelationId = ""
                    locker.Status.ReservedUntilUtc = Nothing

                Next

                db.SaveChanges()

            End Using

            If openResults IsNot Nothing Then

                Dim actionService As New LockerActionService()

                For Each result In openResults

                    If result IsNot Nothing AndAlso result.Success Then
                        Await actionService.
                MarkDoorClosedAndLocalStateUpdatedAsync(result.JournalId)
                    End If

                Next

                Await FinalizeSuccessfulLockerActionsAsync(
        openResults,
        "Pickup completed successfully.")

            End If

        End Function
        Private Sub ReserveLockerForDelivery(
    lockerNumber As String,
    workOrderNumber As String,
    Optional sizeCode As String = Nothing,
    Optional correlationId As String = Nothing
)

            Dim ln As String = (If(lockerNumber, "")).Trim()
            Dim wo As String = (If(workOrderNumber, "")).Trim()
            Dim corr As String = (If(correlationId, "")).Trim()

            If ln.Length = 0 Then
                Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
            End If

            If wo.Length = 0 Then
                Throw New ArgumentException("workOrderNumber / assetTag is required.", NameOf(workOrderNumber))
            End If

            If corr.Length = 0 Then
                corr = Guid.NewGuid().ToString("N")
            End If

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(x) x.LockerNumber = ln)

                If locker Is Nothing Then
                    Throw New InvalidOperationException($"Locker {ln} was not found.")
                End If

                If locker.Status Is Nothing Then
                    Throw New InvalidOperationException($"Locker {ln} does not have a LockerStatus row.")
                End If

                If Not locker.IsEnabled Then
                    Throw New InvalidOperationException($"Locker {ln} is not enabled.")
                End If

                If locker.Status.LockState <> LockState.Closed Then
                    Throw New InvalidOperationException($"Locker {ln} is not closed.")
                End If

                If locker.Status.OccupancyState <> OccupancyState.Vacant Then
                    Throw New InvalidOperationException($"Locker {ln} is not vacant.")
                End If

                If locker.Status.PackagePresent.HasValue AndAlso locker.Status.PackagePresent.Value Then
                    Throw New InvalidOperationException($"Locker {ln} already has package/device present.")
                End If

                Dim nowUtc = DateTime.UtcNow

                If locker.Status.ReservedUntilUtc.HasValue AndAlso
           locker.Status.ReservedUntilUtc.Value > nowUtc Then

                    Throw New InvalidOperationException($"Locker {ln} is already reserved.")
                End If

                ' For asset workflows, sizeCode may be blank because any compartment is acceptable.
                ' For package workflows, caller may still pass the selected sizeCode if useful for audit/debug.
                locker.Status.OccupancyState = OccupancyState.Reserved
                locker.Status.PackagePresent = False
                locker.Status.ReservedWorkOrderNumber = wo
                locker.Status.LastWorkOrderNumber = wo
                locker.Status.ReservedCorrelationId = corr
                locker.Status.ReservedUntilUtc = nowUtc.AddMinutes(10)
                locker.Status.LastUpdatedUtc = nowUtc
                locker.Status.LastReason =
            If(String.IsNullOrWhiteSpace(sizeCode),
               "LockerReserved",
               $"LockerReserved;SizeCode={sizeCode.Trim()}")

                If _authResult IsNot Nothing Then
                    locker.Status.LastActorId = If(Not String.IsNullOrWhiteSpace(_authResult.ActorId),
                                           _authResult.ActorId,
                                           _authResult.UserId)
                End If

                db.SaveChanges()

            End Using

        End Sub
        Private Sub ReleaseLockerReservation(lockerNumber As String, reason As String)

            Dim ln As String = If(lockerNumber, "").Trim()
            If ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then Return

                locker.Status.OccupancyState = OccupancyState.Vacant
                locker.Status.PackagePresent = False
                locker.Status.ReservedUntilUtc = Nothing
                locker.Status.ReservedCorrelationId = ""
                locker.Status.ReservedWorkOrderNumber = ""
                locker.Status.LastReason = If(reason, "").Trim()
                locker.Status.LastUpdatedUtc = DateTime.UtcNow

                Try
                    db.SaveChanges()
                Catch ex As Exception
                    TraceExceptionDeep("RELEASE_LOCKER_RESERVATION_SAVE_FAILED", ex)
                    Throw
                End Try

            End Using

        End Sub
        Private Sub ReleasePendingReservationSafely(lockerNumber As String, reason As String)
            If String.IsNullOrWhiteSpace(lockerNumber) Then Return

            Try
                ReleaseLockerReservation(lockerNumber, reason)
            Catch ex As Exception
                TraceExceptionDeep("RELEASE_PENDING_RESERVATION_FAILED", ex)
            End Try
        End Sub
        Private Async Function FinalizeSuccessfulLockerActionsAsync(openResults As List(Of LockerActionResult),
                                                            message As String) As Task

            If openResults Is Nothing Then Return

            For Each result In openResults

                If result Is Nothing OrElse Not result.Success Then Continue For

                Await New LockerActionService().
            UpdateJournalStateAsync(
                result.JournalId,
                LockerTransactionState.Completed,
                LockerAckStatus.Succeeded,
                message)

            Next

        End Function
#End Region

#Region "Backend / Locker actions"
        Private Async Function AuthorizeLockerOpenActionForWorkOrderAsync(
    workOrderNumber As String,
    lockerNumber As String,
    correlationId As String,
    sessionUserId As String,
    ct As CancellationToken
) As Task(Of LockerAuthorizeResponseDto)

            Dim wo As String = (If(workOrderNumber, "")).Trim()
            Dim ln As String = (If(lockerNumber, "")).Trim()
            Dim actorId As String = (If(sessionUserId, "")).Trim()
            Dim corr As String = (If(correlationId, "")).Trim()

            If wo.Length = 0 Then Throw New ArgumentException("workOrderNumber is required.", NameOf(workOrderNumber))
            If ln.Length = 0 Then Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
            If actorId.Length = 0 Then Throw New InvalidOperationException("ActorId is required before authorizing work-order locker action.")
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
            .requestedBy = actorId,
            .actorId = actorId,
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

                TraceLogger.Log(
            "WORKORDER LOCKER AUTHORIZE REQUEST: " &
            "actorId=" & actorId &
            "; requestedBy=" & actorId &
            "; workOrder=" & wo &
            "; locker=" & ln &
            "; correlationId=" & corr)

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
        GetWorkflowCancellationToken()
    )
        End Function
        Private Async Function CompleteDeliveryForLockerAsync(workOrderNumber As String,
                                                      lockerNumber As String,
                                                      Optional journalId As Integer? = Nothing) As Task

            Dim wo As String = If(workOrderNumber, "").Trim()
            Dim ln As String = If(lockerNumber, "").Trim()

            If wo.Length = 0 OrElse ln.Length = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim locker = db.Lockers.
            Include(Function(l) l.Status).
            SingleOrDefault(Function(l) l.LockerNumber = ln)

                If locker Is Nothing OrElse locker.Status Is Nothing Then
                    Throw New InvalidOperationException($"Locker {ln} was not found or has no status row.")
                End If

                locker.Status.OccupancyState = OccupancyState.Occupied
                locker.Status.PackagePresent = True
                locker.Status.LastUpdatedUtc = DateTime.UtcNow
                locker.Status.LastReason = "DeliveryCompleted"
                locker.Status.LastActorId = CurrentActorId(ActorType.User)

                locker.Status.LastWorkOrderNumber = wo
                locker.Status.ReservedWorkOrderNumber = ""
                locker.Status.ReservedCorrelationId = ""
                locker.Status.ReservedUntilUtc = Nothing

                db.SaveChanges()

            End Using

            If journalId.HasValue Then

                Dim actionService As New LockerActionService()

                Await actionService.
        MarkDoorClosedAndLocalStateUpdatedAsync(journalId.Value)

                Await actionService.
        UpdateJournalStateAsync(
            journalId.Value,
            LockerTransactionState.Completed,
            LockerAckStatus.Succeeded,
            "Delivery completed successfully.")

            End If

        End Function
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
            Dim processException As Exception = Nothing
            Dim openResult As LockerActionResult = Nothing
            Dim assignedLockerNumber As String = Nothing

            Try
                ShowPrompt($"Finding an available {code} compartment…")

                assignedLockerNumber = _assigner.SelectNextAvailableLockerNumber(code)

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then

                    AuditNoAvailability(actionId, wo, code)
                    ShowPrompt($"No {code} compartments are available. Select a different size.")

                    Await Task.Delay(1200)

                    If myEpoch <> _uiEpoch Then Return

                    _state = ScreenState.AwaitLockerSize
                    ShowLockerSizeSelection($"No {code} compartments are available. Select a different size.")
                    If CompleteDeferredReturnToStartIfRequested() Then Return
                    Return

                End If

                ReserveLockerForDelivery(
            lockerNumber:=assignedLockerNumber,
            workOrderNumber:=wo,
            sizeCode:=code,
            correlationId:=actionId)

                AuditAssignSucceeded(actionId, wo, code, assignedLockerNumber)

                ShowPrompt($"Authorizing locker {assignedLockerNumber}…")

                Dim authorizeResponse As LockerAuthorizeResponseDto =
            Await AuthorizeLockerOpenActionForWorkOrderAsync(
                workOrderNumber:=wo,
                lockerNumber:=assignedLockerNumber,
                correlationId:=actionId,
                sessionUserId:=If(_courierAuth?.UserId, _authResult?.UserId),
                ct:=GetWorkflowCancellationToken())

                If myEpoch <> _uiEpoch Then Return

                If authorizeResponse Is Nothing OrElse
           authorizeResponse.authorization Is Nothing OrElse
           Not authorizeResponse.authorization.isAuthorized Then

                    ReleaseLockerReservation(assignedLockerNumber, "DeliveryAuthorizeDenied")
                    ShowPrompt($"Delivery was not authorized for locker {assignedLockerNumber}.")
                    endSessionAfterDelay = True

                Else

                    ShowPrompt($"Opening locker {assignedLockerNumber}…")

                    openResult =
                Await TryOpenLockerWithJournalAsync(actionId, wo, assignedLockerNumber)

                    Dim opened As Boolean = openResult IsNot Nothing AndAlso openResult.Success

                    If opened Then

                        Await AckLockerActionSafeAsync(
                    authorizeResponse:=authorizeResponse,
                    correlationId:=actionId,
                    compartmentIds:=New List(Of String) From {assignedLockerNumber},
                    ackStatus:="executed",
                    hardwareEventCode:="LOCKER_OPEN_OK",
                    message:=$"Locker {assignedLockerNumber} opened for delivery.",
                    ct:=GetWorkflowCancellationToken())

                        ShowPrompt($"Locker {assignedLockerNumber} opened. Place contents inside and close the door.")

                        Dim closedOk =
                    Await WaitForLockerClosedAsync(
                        lockerNumber:=assignedLockerNumber,
                        timeoutMs:=120000,
                        myEpoch:=myEpoch)

                        If myEpoch <> _uiEpoch Then Return

                        If Not closedOk Then

                            ShowPrompt($"Please close locker {assignedLockerNumber} to continue.")
                            endSessionAfterDelay = True

                        Else

                            Await CompleteDeliveryForLockerAsync(
    wo,
    assignedLockerNumber,
    If(openResult Is Nothing, CType(Nothing, Integer?), openResult.JournalId))

                            Dim deliverAnother =
                        Await PromptDeliverAnotherAsync(
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
                    ct:=GetWorkflowCancellationToken())

                        ReleaseLockerReservation(assignedLockerNumber, "DeliveryOpenFailed")
                        endSessionAfterDelay = True

                    End If

                End If

            Catch ex As Exception

                processException = ex

            End Try

            If processException IsNot Nothing Then

                If myEpoch <> _uiEpoch Then Return

                If openResult Is Nothing OrElse Not openResult.Success Then
                    ReleasePendingReservationSafely(
                        assignedLockerNumber,
                        "DeliveryAssignmentException")
                End If

                errorMessage = $"System unavailable: {processException.Message}"
                endSessionAfterDelay = True

                If openResult IsNot Nothing Then

                    Try
                        Await New LockerActionService().
                    UpdateJournalStateAsync(
                        openResult.JournalId,
                        LockerTransactionState.NeedsReconciliation,
                        LockerAckStatus.Failed,
                        processException.Message)

                    Catch updateEx As Exception
                        TraceExceptionDeep("DELIVERY_JOURNAL_UPDATE_FAILED", updateEx)
                    End Try

                End If

            End If

            If myEpoch = _uiEpoch Then
                FocusHidSink()
            End If

            If myEpoch <> _uiEpoch Then Return

            If Not String.IsNullOrWhiteSpace(errorMessage) Then
                ShowPrompt("*" & errorMessage)
            End If

            If endSessionAfterDelay Then

                Await Task.Delay(2500)

                If myEpoch = _uiEpoch Then
                    EndDeliverySession()
                End If

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

            If _state <> ScreenState.AwaitLockerSize Then
                ShowPrompt("Selection ignored: not awaiting locker size.")
                Return False
            End If

            If _activeWorkflow Is Nothing Then
                ShowPrompt("No active workflow. Returning to start.")
                ResetToAwaitWorkflowChoice()
                Return False
            End If

            Dim mode As String = If(_activeWorkflow.Mode, "").Trim().ToLowerInvariant()
            Dim action As String = GetWorkflowAction(_activeWorkflow)

            If mode <> "package_workflow" OrElse action <> "stage" Then
                ShowPrompt("Size selection is not valid for this workflow.")
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
        Private Sub ShowLockerSizeSelection(Optional promptText As String = Nothing)

            EnsureSizeTilesLoaded()
            BindSizeTilesToUi()

            _selectedSizeCode = Nothing

            If _sizeTiles Is Nothing OrElse _sizeTiles.Count = 0 Then

                SizeSelectionPanel.Visibility = Visibility.Collapsed
                ShowPrompt("No commissioned compartment sizes are available. Returning to start.")

                Dim myEpoch As Integer = _uiEpoch
                Dispatcher.BeginInvoke(
                    Async Sub()
                        Await Task.Delay(2500)
                        If myEpoch = _uiEpoch Then
                            ResetToAwaitWorkflowChoice()
                        End If
                    End Sub)
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

            Dim resolvedPrompt As String = promptText

            If String.IsNullOrWhiteSpace(resolvedPrompt) Then

                If _activeStep IsNot Nothing AndAlso
           Not String.IsNullOrWhiteSpace(_activeStep.Prompt) Then

                    resolvedPrompt = _activeStep.Prompt.Trim()

                Else

                    resolvedPrompt = "Select option."

                End If

            End If

            ShowPrompt(resolvedPrompt)

        End Sub
        Private Async Sub ProcessPackageStageAssignmentAsync()

            Dim myEpoch As Integer = _uiEpoch
            Dim actionId As String = Guid.NewGuid().ToString("N")
            Dim processException As Exception = Nothing
            Dim openResult As LockerActionResult = Nothing

            If Not ValidateDeliveryWorkOrder() Then Return

            Dim referenceNumber As String = _activeWorkOrder.WorkOrderNumber

            _state = ScreenState.ValidatingCredential
            SetUiEnabled(False)
            HideLockerSizeSelection()

            Dim assignedLockerNumber As String = Nothing
            Dim endSessionAfterDelay As Boolean = False
            Dim errorMessage As String = Nothing

            Try
                ShowPrompt("Finding an available compartment...")

                assignedLockerNumber = _assigner.SelectNextAvailableAssetLockerNumber()

                If String.IsNullOrWhiteSpace(assignedLockerNumber) Then
                    ShowPrompt("No compartments are currently available.")
                    endSessionAfterDelay = True
                Else
                    ReserveLockerForDelivery(
                lockerNumber:=assignedLockerNumber,
                workOrderNumber:=referenceNumber,
                correlationId:=actionId)

                    ShowPrompt($"Opening locker {assignedLockerNumber}...")

                    openResult =
                Await TryOpenLockerWithJournalAsync(
                    actionId,
                    referenceNumber,
                    assignedLockerNumber)

                    If openResult Is Nothing OrElse Not openResult.Success Then

                        ReleaseLockerReservation(assignedLockerNumber, "PackageStageOpenFailed")
                        ShowPrompt($"Locker {assignedLockerNumber} could not be opened.")
                        endSessionAfterDelay = True

                    Else

                        ShowPrompt($"Locker {assignedLockerNumber} opened. Place contents inside and close the door.")

                        Dim closedOk As Boolean =
                    Await WaitForLockerClosedAsync(
                        lockerNumber:=assignedLockerNumber,
                        timeoutMs:=120000,
                        myEpoch:=myEpoch)

                        If myEpoch <> _uiEpoch Then Return

                        If Not closedOk Then

                            ShowPrompt($"Please close locker {assignedLockerNumber} to continue.")
                            endSessionAfterDelay = True

                        Else

                            Await CompleteDeliveryForLockerAsync(
                        referenceNumber,
                        assignedLockerNumber,
                        If(openResult Is Nothing, CType(Nothing, Integer?), openResult.JournalId))

                            Dim deliverAnother =
                        Await PromptDeliverAnotherAsync(
                            timeoutSeconds:=15,
                            myEpoch:=myEpoch)

                            If myEpoch <> _uiEpoch Then Return

                            If deliverAnother.HasValue AndAlso deliverAnother.Value Then
                                ContinueDeliverySession()
                                Return
                            End If

                            EndDeliverySession()
                            Return

                        End If

                    End If

                End If

            Catch ex As Exception
                processException = ex
            End Try

            If processException IsNot Nothing Then

                errorMessage = $"System unavailable: {processException.Message}"

                If openResult Is Nothing OrElse Not openResult.Success Then
                    ReleasePendingReservationSafely(
                        assignedLockerNumber,
                        "PackageStageException")
                End If

                If openResult IsNot Nothing Then
                    Try
                        Await New LockerActionService().
                    UpdateJournalStateAsync(
                        openResult.JournalId,
                        LockerTransactionState.NeedsReconciliation,
                        LockerAckStatus.Failed,
                        processException.Message)

                    Catch updateEx As Exception
                        TraceExceptionDeep("PACKAGE_STAGE_JOURNAL_UPDATE_FAILED", updateEx)
                    End Try
                End If

                endSessionAfterDelay = True

            End If

            If myEpoch = _uiEpoch Then
                FocusHidSink()
            End If

            If myEpoch <> _uiEpoch Then Return

            If Not String.IsNullOrWhiteSpace(errorMessage) Then
                ShowPrompt("*" & errorMessage)
            End If

            If endSessionAfterDelay Then
                Await Task.Delay(2500)

                If myEpoch = _uiEpoch Then
                    EndDeliverySession()
                End If
            End If

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
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] inst={_instanceId} state={_state} epoch={_uiEpoch}{Environment.NewLine}" &
                    Environment.StackTrace & Environment.NewLine & Environment.NewLine
                TraceLogger.AppendRetainedText(path, line)
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



