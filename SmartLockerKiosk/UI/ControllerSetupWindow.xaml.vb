Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO.Ports
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk
    Partial Public Class ControllerSetupWindow
        Inherits Window

        Public Property IsCommissioningMode As Boolean = True
        Public Property ActorId As String

        Private _candidates As List(Of PortCandidate) = New List(Of PortCandidate)()
        Private Const HldBaudRate As Integer = 115200

        Private _setupSaved As Boolean = False
        Private _controllersDetected As Boolean = False
        Private _branchAActivelySelected As Boolean = False
        Private _branchBActivelySelected As Boolean = False

        Private _pulseTimer As DispatcherTimer = Nothing
        Private _pulseTargetControl As Control = Nothing
        Private _pulseIsOn As Boolean = False
        Private _pulseNormalBackgroundBrush As Brush = Nothing
        Private _pulseNormalBorderBrush As Brush = Nothing

        Private ReadOnly _pulseHighlightBrush As Brush =
        New SolidColorBrush(Color.FromRgb(255, 191, 0))

        Public Sub New()
            InitializeComponent()
        End Sub
        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            If String.IsNullOrWhiteSpace(ActorId) Then
                ActorId = "System:Commissioning"
            End If

            LoadSavedButton.Visibility = If(IsCommissioningMode, Visibility.Collapsed, Visibility.Visible)

            _setupSaved = False
            _controllersDetected = False
            _branchAActivelySelected = False
            _branchBActivelySelected = False

            BranchAComboBox.SelectedItem = Nothing
            BranchBComboBox.SelectedItem = Nothing
            PortsListBox.ItemsSource = Nothing

            If Not IsCommissioningMode Then
                LoadControllerPortsFromDb()
            Else
                StatusText.Text = "Step 1: Detect controllers. Step 2: Select Branch A. Step 3: Select Branch B if applicable. Step 4: Save & Continue."
            End If

            UpdateStepGuidance()
        End Sub

        ' -------- UI events --------
        Private Sub LoadSaved_Click(sender As Object, e As RoutedEventArgs) Handles LoadSavedButton.Click
            LoadControllerPortsFromDb()
        End Sub
        Private Sub DetectControllers_Click(sender As Object, e As RoutedEventArgs) Handles DetectButton.Click
            Try
                StatusText.Text = "Detecting COM ports…"

                Dim ports = SerialPort.GetPortNames().
                OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase).
                ToList()

                PortsListBox.ItemsSource = ports
                BranchAComboBox.ItemsSource = ports
                BranchBComboBox.ItemsSource = ports

                If ports.Count = 0 Then
                    _controllersDetected = False
                    _branchAActivelySelected = False
                    _branchBActivelySelected = False
                    BranchAComboBox.SelectedItem = Nothing
                    BranchBComboBox.SelectedItem = Nothing

                    StatusText.Text = "No COM ports detected. Check USB/RS-232 adapter, drivers, and Device Manager."
                    UpdateStepGuidance()
                    Return
                End If

                _controllersDetected = True
                _branchAActivelySelected = False
                _branchBActivelySelected = False

                ' Require active selection by the user
                BranchAComboBox.SelectedItem = Nothing
                BranchBComboBox.SelectedItem = Nothing

                StatusText.Text = $"Detected {ports.Count} COM port(s). Select Branch A{If(IsBranchBApplicable(), ", Branch B,", "")} then Save & Continue."
                UpdateStepGuidance()

            Catch ex As Exception
                _controllersDetected = False
                _branchAActivelySelected = False
                _branchBActivelySelected = False
                StatusText.Text = $"Detect failed: {ex.GetType().Name}: {ex.Message}"
                UpdateStepGuidance()
            End Try
        End Sub
        Private Sub BranchAComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles BranchAComboBox.SelectionChanged
            If Me.IsLoaded Then
                _branchAActivelySelected = BranchAComboBox.SelectedItem IsNot Nothing
                UpdateStepGuidance()
            End If
        End Sub
        Private Sub BranchBComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles BranchBComboBox.SelectionChanged
            If Me.IsLoaded Then
                _branchBActivelySelected = BranchBComboBox.SelectedItem IsNot Nothing
                UpdateStepGuidance()
            End If
        End Sub
        Private Sub SavePorts_Click(sender As Object, e As RoutedEventArgs) Handles SaveButton.Click
            StopPulse()

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim aPort As String = If(TryCast(BranchAComboBox.SelectedItem, String), "").Trim()
            Dim bPort As String = If(TryCast(BranchBComboBox.SelectedItem, String), "").Trim()

            ' --- Validation ---
            If Not _controllersDetected Then
                StatusText.Text = "Please detect controllers first."
                UpdateStepGuidance()
                Return
            End If

            If Not _branchAActivelySelected OrElse String.IsNullOrWhiteSpace(aPort) Then
                StatusText.Text = "Branch A selection is required."
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = ActorId,
                .AffectedComponent = "ControllerSetupWindow",
                .Outcome = Audit.AuditOutcome.Denied,
                .CorrelationId = actionId,
                .ReasonCode = "SavePortsValidationFailed:BranchARequired"
            })
                UpdateStepGuidance()
                Return
            End If

            If IsBranchBApplicable() AndAlso (Not _branchBActivelySelected OrElse String.IsNullOrWhiteSpace(bPort)) Then
                StatusText.Text = "Branch B selection is required."
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = ActorId,
                .AffectedComponent = "ControllerSetupWindow",
                .Outcome = Audit.AuditOutcome.Denied,
                .CorrelationId = actionId,
                .ReasonCode = "SavePortsValidationFailed:BranchBRequired"
            })
                UpdateStepGuidance()
                Return
            End If

            If String.Equals(aPort, bPort, StringComparison.OrdinalIgnoreCase) Then
                StatusText.Text = "Branch B must be different than Branch A."
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = ActorId,
                .AffectedComponent = "ControllerSetupWindow",
                .Outcome = Audit.AuditOutcome.Denied,
                .CorrelationId = actionId,
                .ReasonCode = "SavePortsValidationFailed:BranchBDuplicate"
            })
                UpdateStepGuidance()
                Return
            End If

            ' --- UI gating to prevent double-save ---
            Dim priorDetectEnabled = DetectButton.IsEnabled
            Dim priorLoadEnabled = LoadSavedButton.IsEnabled
            Dim priorSaveEnabled = SaveButton.IsEnabled
            Dim priorBranchAEnabled = BranchAComboBox.IsEnabled
            Dim priorBranchBEnabled = BranchBComboBox.IsEnabled

            DetectButton.IsEnabled = False
            LoadSavedButton.IsEnabled = False
            SaveButton.IsEnabled = False
            BranchAComboBox.IsEnabled = False
            BranchBComboBox.IsEnabled = False

            Try
                StatusText.Text = "Saving controller ports…"

                Dim beforeA As String = Nothing
                Dim beforeB As String = Nothing
                Dim beforeBEnabled As Boolean? = Nothing

                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim rows = db.ControllerPorts.ToList()
                    Dim aRow = rows.SingleOrDefault(Function(r) r.BranchName = "A")
                    Dim bRow = rows.SingleOrDefault(Function(r) r.BranchName = "B")

                    beforeA = If(aRow?.PortName, Nothing)
                    beforeB = If(bRow?.PortName, Nothing)
                    beforeBEnabled = If(bRow Is Nothing, CType(Nothing, Boolean?), bRow.IsEnabled)

                    UpsertControllerPort(db, "A", aPort, True)
                    UpsertControllerPort(db, "B", bPort, True)

                    db.SaveChanges()
                End Using

                Dim changes As New List(Of String)

                If Not String.Equals(If(beforeA, ""), aPort, StringComparison.OrdinalIgnoreCase) Then
                    changes.Add($"A:{If(beforeA, "(none)")}→{aPort}")
                End If

                If Not String.Equals(If(beforeB, ""), bPort, StringComparison.OrdinalIgnoreCase) Then
                    changes.Add($"B:{If(beforeB, "(none)")}→{bPort}")
                End If

                Dim newBEnabled As Boolean = True
                If beforeBEnabled.HasValue AndAlso beforeBEnabled.Value <> newBEnabled Then
                    changes.Add($"BEnabled:{beforeBEnabled.Value}→{newBEnabled}")
                End If

                Dim changeSummary As String =
                If(changes.Count = 0,
                   "ControllerPortMappingSaved:NoChange",
                   "ControllerPortMappingSaved:" & String.Join(";", changes))

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = ActorId,
                .AffectedComponent = "ControllerSetupWindow",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = actionId,
                .ReasonCode = changeSummary
            })

                StatusText.Text = $"Saved. Branch A = {aPort}, Branch B = {bPort}. Continuing…"

                _setupSaved = True
                StopPulse()

                Try
                    Me.DialogResult = True
                Catch
                    ' If not shown as dialog, ignore
                End Try

                Me.Close()

            Catch ex As Exception
                StatusText.Text = $"Save failed: {ex.GetType().Name}: {ex.Message}"
                Debug.WriteLine("ControllerSetup Save failed: " & ex.ToString())

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = ActorId,
                .AffectedComponent = "ControllerSetupWindow",
                .Outcome = Audit.AuditOutcome.Error,
                .CorrelationId = actionId,
                .ReasonCode = "ControllerPortMappingSaveFailed:" & ex.GetType().Name
            })

                _setupSaved = False
                UpdateStepGuidance()

            Finally
                If Me.IsVisible Then
                    DetectButton.IsEnabled = priorDetectEnabled
                    LoadSavedButton.IsEnabled = priorLoadEnabled
                    SaveButton.IsEnabled = priorSaveEnabled
                    BranchAComboBox.IsEnabled = priorBranchAEnabled
                    BranchBComboBox.IsEnabled = priorBranchBEnabled
                    UpdateStepGuidance()
                End If
            End Try
        End Sub
        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            StopPulse()

            Try
                Me.DialogResult = False
            Catch
                ' If not shown as dialog, ignore
            End Try

            Me.Close()
        End Sub
        Private Sub Cancel_Click(sender As Object, e As RoutedEventArgs)
            StopPulse()
            Me.Close()
        End Sub
        Protected Overrides Sub OnClosed(e As EventArgs)
            StopPulse()
            MyBase.OnClosed(e)
        End Sub
        Private Shared Async Function WaitWithTimeoutAsync(task As Task, timeout As TimeSpan) As Task(Of Boolean)
            Dim delay = Task.Delay(timeout)
            Dim completed = Await Task.WhenAny(task, delay).ConfigureAwait(True)
            Return completed Is task
        End Function
        Private Sub LoadControllerPortsFromDb()
            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim rows = db.ControllerPorts.AsNoTracking().ToList()

                    Dim a = rows.FirstOrDefault(Function(r) r.BranchName = "A")
                    Dim b = rows.FirstOrDefault(Function(r) r.BranchName = "B")

                    If a IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(a.PortName) Then
                        If BranchAComboBox.ItemsSource Is Nothing AndAlso Not BranchAComboBox.Items.Contains(a.PortName) Then
                            BranchAComboBox.Items.Add(a.PortName)
                        End If
                        BranchAComboBox.SelectedItem = a.PortName
                        _branchAActivelySelected = True
                    End If

                    If b IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(b.PortName) Then
                        If BranchBComboBox.ItemsSource Is Nothing AndAlso Not BranchBComboBox.Items.Contains(b.PortName) Then
                            BranchBComboBox.Items.Add(b.PortName)
                        End If
                        BranchBComboBox.SelectedItem = b.PortName
                        _branchBActivelySelected = True
                    End If

                    _controllersDetected = (a IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(a.PortName))
                End Using

                StatusText.Text = "Loaded saved controller ports from DB."
                UpdateStepGuidance()

            Catch ex As Exception
                StatusText.Text = $"Load ports failed: {ex.Message}"
                UpdateStepGuidance()
            End Try
        End Sub
        Private Sub UpsertControllerPort(db As KioskDbContext, branch As String, portName As String, enabled As Boolean)
            Dim row = db.ControllerPorts.SingleOrDefault(Function(r) r.BranchName = branch)
            If row Is Nothing Then
                row = New ControllerPort With {.BranchName = branch}
                db.ControllerPorts.Add(row)
            End If

            row.PortName = portName
            row.IsEnabled = enabled
            row.LastVerifiedUtc = DateTime.UtcNow
        End Sub
        Private Function IsBranchBApplicable() As Boolean
            Return PortsListBox.Items IsNot Nothing AndAlso PortsListBox.Items.Count > 1
        End Function
        Private Function CanSaveControllerSetup() As Boolean
            Dim aPort As String = If(TryCast(BranchAComboBox.SelectedItem, String), "").Trim()
            Dim bPort As String = If(TryCast(BranchBComboBox.SelectedItem, String), "").Trim()

            If Not _controllersDetected Then Return False
            If Not _branchAActivelySelected Then Return False
            If String.IsNullOrWhiteSpace(aPort) Then Return False

            If IsBranchBApplicable() Then
                If Not _branchBActivelySelected Then Return False
                If String.IsNullOrWhiteSpace(bPort) Then Return False
            End If

            If String.Equals(aPort, bPort, StringComparison.OrdinalIgnoreCase) Then Return False

            Return True
        End Function
        Private Sub UpdateStepGuidance()
            If _setupSaved Then
                StopPulse()
                Return
            End If

            If Not _controllersDetected Then
                StartControlPulse(DetectButton)
                Return
            End If

            If Not _branchAActivelySelected OrElse BranchAComboBox.SelectedItem Is Nothing Then
                StartControlPulse(BranchAComboBox)
                Return
            End If

            If IsBranchBApplicable() Then
                If Not _branchBActivelySelected OrElse BranchBComboBox.SelectedItem Is Nothing Then
                    StartControlPulse(BranchBComboBox)
                    Return
                End If
            End If

            If CanSaveControllerSetup() Then
                StartControlPulse(SaveButton)
            Else
                StopPulse()
            End If
        End Sub
        Private Sub StartControlPulse(target As Control)
            If target Is Nothing Then Return

            If Object.ReferenceEquals(_pulseTargetControl, target) AndAlso _pulseTimer IsNot Nothing Then
                Return
            End If

            StopPulse()

            _pulseTargetControl = target
            _pulseIsOn = False
            _pulseNormalBackgroundBrush = target.Background
            _pulseNormalBorderBrush = target.BorderBrush

            _pulseTimer = New DispatcherTimer()
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(450)

            AddHandler _pulseTimer.Tick,
            Sub()
                If _pulseTargetControl Is Nothing Then Return

                _pulseIsOn = Not _pulseIsOn

                If _pulseIsOn Then
                    _pulseTargetControl.Background = _pulseHighlightBrush
                    _pulseTargetControl.BorderBrush = Brushes.DarkOrange
                Else
                    _pulseTargetControl.Background = _pulseNormalBackgroundBrush
                    _pulseTargetControl.BorderBrush = _pulseNormalBorderBrush
                End If
            End Sub

            _pulseTimer.Start()
        End Sub
        Private Sub StopPulse()
            If _pulseTimer IsNot Nothing Then
                _pulseTimer.Stop()
                _pulseTimer = Nothing
            End If

            If _pulseTargetControl IsNot Nothing Then
                _pulseTargetControl.Background = _pulseNormalBackgroundBrush
                _pulseTargetControl.BorderBrush = _pulseNormalBorderBrush
            End If

            _pulseTargetControl = Nothing
            _pulseNormalBackgroundBrush = Nothing
            _pulseNormalBorderBrush = Nothing
            _pulseIsOn = False
        End Sub

    End Class
End Namespace

