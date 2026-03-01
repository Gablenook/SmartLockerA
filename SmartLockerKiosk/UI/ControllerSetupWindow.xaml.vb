Imports System.IO.Ports
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports HldRelayController
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk

    Partial Public Class ControllerSetupWindow
        Inherits Window

        Private _candidates As List(Of PortCandidate) = New List(Of PortCandidate)()
        Public Property ActorId As String
        Private Const HldBaudRate As Integer = 115200

        Public Sub New()
            InitializeComponent()

            ' Ensure Branch B can be blank
            If Not BranchBComboBox.Items.Contains("") Then
                BranchBComboBox.Items.Add("")
            End If
            BranchBComboBox.SelectedItem = ""

            ' Preload saved ports
            LoadControllerPortsFromDb()
        End Sub
        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            If String.IsNullOrWhiteSpace(ActorId) Then
                ActorId = "System:Commissioning"
            End If
        End Sub

        ' -------- UI events --------
        Private Sub LoadSaved_Click(sender As Object, e As RoutedEventArgs)
            LoadControllerPortsFromDb()
        End Sub


        Private Sub DetectControllers_Click(sender As Object, e As RoutedEventArgs)
            Try
                StatusText.Text = "Detecting COM ports…"

                Dim ports = SerialPort.GetPortNames().
            OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase).
            ToList()

                PortsListBox.ItemsSource = ports

                ' Populate dropdowns too
                BranchAComboBox.ItemsSource = ports
                BranchBComboBox.ItemsSource = ports

                If ports.Count = 0 Then
                    StatusText.Text = "No COM ports detected. Check USB/RS-232 adapter, drivers, and Device Manager."
                    Return
                End If

                ' Optional: preselect first port for A if none selected
                If BranchAComboBox.SelectedItem Is Nothing Then
                    BranchAComboBox.SelectedIndex = 0
                End If

                StatusText.Text = $"Detected {ports.Count} COM port(s). Select Branch A/B and Save & Continue."
            Catch ex As Exception
                StatusText.Text = $"Detect failed: {ex.GetType().Name}: {ex.Message}"
            End Try
        End Sub
        Private Sub SavePorts_Click(sender As Object, e As RoutedEventArgs)
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Dim aPort As String = If(TryCast(BranchAComboBox.SelectedItem, String), "").Trim()
            Dim bPort As String = If(TryCast(BranchBComboBox.SelectedItem, String), "").Trim()

            ' --- Validation ---
            If String.IsNullOrWhiteSpace(aPort) Then
                StatusText.Text = "Branch A is required."
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = ActorId,
            .AffectedComponent = "ControllerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "SavePortsValidationFailed:BranchARequired"
        })
                Return
            End If

            If Not String.IsNullOrWhiteSpace(bPort) AndAlso
       String.Equals(aPort, bPort, StringComparison.OrdinalIgnoreCase) Then

                StatusText.Text = "Branch B must be different than Branch A (or blank)."
                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = ActorId,
            .AffectedComponent = "ControllerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "SavePortsValidationFailed:BranchBDuplicate"
        })
                Return
            End If

            ' --- UI gating to prevent double-save ---
            Dim priorDetectEnabled = DetectButton.IsEnabled
            Dim priorLoadEnabled = LoadSavedButton.IsEnabled
            Dim priorSaveEnabled = SaveButton.IsEnabled

            DetectButton.IsEnabled = False
            LoadSavedButton.IsEnabled = False
            SaveButton.IsEnabled = False

            Try
                StatusText.Text = "Saving controller ports…"

                ' Capture "before" values for audit delta
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

                    ' Apply new values
                    UpsertControllerPort(db, "A", aPort, True)

                    If String.IsNullOrWhiteSpace(bPort) Then
                        UpsertControllerPort(db, "B", Nothing, False)
                    Else
                        UpsertControllerPort(db, "B", bPort, True)
                    End If

                    db.SaveChanges()
                End Using

                Dim bDisplay = If(String.IsNullOrWhiteSpace(bPort), "(none)", bPort)
                StatusText.Text = $"Saved. Branch A = {aPort}, Branch B = {bDisplay}. Continuing…"

                ' Build a small "what changed" summary (no secrets; port names are OK here)
                Dim changes As New List(Of String)

                If Not String.Equals(If(beforeA, ""), aPort, StringComparison.OrdinalIgnoreCase) Then
                    changes.Add($"A:{If(beforeA, "(none)")}→{aPort}")
                End If

                Dim newB As String = If(String.IsNullOrWhiteSpace(bPort), Nothing, bPort)
                If Not String.Equals(If(beforeB, ""), If(newB, ""), StringComparison.OrdinalIgnoreCase) Then
                    changes.Add($"B:{If(beforeB, "(none)")}→{If(newB, "(none)")}")
                End If

                Dim newBEnabled As Boolean = Not String.IsNullOrWhiteSpace(bPort)
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

                ' "Save & Continue": close as success so caller can proceed
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

            Finally
                ' Restore buttons if we didn't close
                If Me.IsVisible Then
                    DetectButton.IsEnabled = priorDetectEnabled
                    LoadSavedButton.IsEnabled = priorLoadEnabled
                    SaveButton.IsEnabled = priorSaveEnabled
                End If
            End Try
        End Sub
        Private Shared Async Function WaitWithTimeoutAsync(task As Task, timeout As TimeSpan) As Task(Of Boolean)
            Dim delay = Task.Delay(timeout)
            Dim completed = Await Task.WhenAny(task, delay).ConfigureAwait(True)
            Return completed Is task
        End Function
        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            ' Treat Close/Back/Cancel as "step not completed"
            Try
                Me.DialogResult = False
            Catch
                ' If not shown as dialog, ignore
            End Try

            Me.Close()
        End Sub
        Private Sub LoadControllerPortsFromDb()
            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim rows = db.ControllerPorts.AsNoTracking().ToList()

                    Dim a = rows.FirstOrDefault(Function(r) r.BranchName = "A")
                    Dim b = rows.FirstOrDefault(Function(r) r.BranchName = "B")

                    If a IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(a.PortName) Then
                        If Not BranchAComboBox.Items.Contains(a.PortName) Then BranchAComboBox.Items.Add(a.PortName)
                        BranchAComboBox.SelectedItem = a.PortName
                    End If

                    If Not BranchBComboBox.Items.Contains("") Then BranchBComboBox.Items.Add("")
                    If b IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(b.PortName) Then
                        If Not BranchBComboBox.Items.Contains(b.PortName) Then BranchBComboBox.Items.Add(b.PortName)
                        BranchBComboBox.SelectedItem = b.PortName
                    Else
                        BranchBComboBox.SelectedItem = ""
                    End If
                End Using

                StatusText.Text = "Loaded saved controller ports from DB."
            Catch ex As Exception
                StatusText.Text = $"Load ports failed: {ex.Message}"
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

    End Class

End Namespace

