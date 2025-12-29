Imports System.Windows
Imports HldSerialLib.Serial.LockBoard
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk

    Partial Public Class ControllerSetupWindow
        Inherits Window

        Private _candidates As List(Of PortCandidate) = New List(Of PortCandidate)()

        ' HLD comm settings (from your spec)
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

        ' -------- UI events --------

        Private Sub LoadSaved_Click(sender As Object, e As RoutedEventArgs)
            LoadControllerPortsFromDb()
        End Sub

        Private Sub DetectControllers_Click(sender As Object, e As RoutedEventArgs)
            StatusText.Text = "Scanning serial ports…"

            Try
                Dim scanner As New HldPortScanner()
                _candidates = scanner.ScanCandidates()

                PortsListBox.Items.Clear()

                ' Preserve any current selections, but refresh the list of options
                Dim prevA As String = TryCast(BranchAComboBox.SelectedItem, String)
                Dim prevB As String = TryCast(BranchBComboBox.SelectedItem, String)

                BranchAComboBox.Items.Clear()

                ' Keep the blank option for B
                Dim keepBlank As Boolean = True
                BranchBComboBox.Items.Clear()
                If keepBlank Then BranchBComboBox.Items.Add("")

                If _candidates Is Nothing OrElse _candidates.Count = 0 Then
                    StatusText.Text = "No controller-like ports detected. Check USB/RS232 adapter and wiring."
                    Return
                End If

                For Each c In _candidates.OrderByDescending(Function(x) x.Score)
                    PortsListBox.Items.Add($"{c.PortName}  (score {c.Score})")
                    BranchAComboBox.Items.Add(c.PortName)
                    BranchBComboBox.Items.Add(c.PortName)
                Next

                ' Auto-select best candidate for A if nothing chosen
                If Not String.IsNullOrWhiteSpace(prevA) AndAlso BranchAComboBox.Items.Contains(prevA) Then
                    BranchAComboBox.SelectedItem = prevA
                Else
                    BranchAComboBox.SelectedIndex = 0
                End If

                ' Restore B if possible, else blank
                If Not String.IsNullOrWhiteSpace(prevB) AndAlso BranchBComboBox.Items.Contains(prevB) Then
                    BranchBComboBox.SelectedItem = prevB
                Else
                    BranchBComboBox.SelectedItem = ""
                End If

                StatusText.Text = $"Found {_candidates.Count} candidate port(s). Select Branch A/B then Save."
            Catch ex As Exception
                StatusText.Text = $"Scan failed: {ex.Message}"
            End Try
        End Sub

        Private Sub SavePorts_Click(sender As Object, e As RoutedEventArgs)
            Dim aPort As String =
    If(TryCast(BranchAComboBox.SelectedItem, String), "").Trim()

            Dim bPort As String =
    If(TryCast(BranchBComboBox.SelectedItem, String), "").Trim()

            If String.IsNullOrWhiteSpace(aPort) Then
                StatusText.Text = "Branch A is required."
                Return
            End If

            If Not String.IsNullOrWhiteSpace(bPort) AndAlso
               String.Equals(aPort, bPort, StringComparison.OrdinalIgnoreCase) Then
                StatusText.Text = "Branch B must be different than Branch A (or blank)."
                Return
            End If

            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    UpsertControllerPort(db, "A", aPort, True)

                    If String.IsNullOrWhiteSpace(bPort) Then
                        UpsertControllerPort(db, "B", Nothing, False)
                    Else
                        UpsertControllerPort(db, "B", bPort, True)
                    End If

                    db.SaveChanges()
                End Using

                StatusText.Text =
                    $"Saved to DB. A={aPort}  B={(If(String.IsNullOrWhiteSpace(bPort), "(none)", bPort))}"
            Catch ex As Exception
                StatusText.Text = $"Save failed: {ex.Message}"
            End Try
        End Sub

        Private Sub TestA_Click(sender As Object, e As RoutedEventArgs)
            Dim port As String = If(TryCast(BranchAComboBox.SelectedItem, String), "").Trim()
            If String.IsNullOrWhiteSpace(port) Then
                StatusText.Text = "Select a port for Branch A first."
                Return
            End If

            TestPort(port, "A")
        End Sub

        Private Sub TestB_Click(sender As Object, e As RoutedEventArgs)
            Dim port As String = If(TryCast(BranchBComboBox.SelectedItem, String), "").Trim()
            If String.IsNullOrWhiteSpace(port) Then
                StatusText.Text = "Branch B is blank (none). Select a port to test."
                Return
            End If

            TestPort(port, "B")
        End Sub

        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub

        ' -------- DB helpers --------

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

        ' -------- Test logic --------

        Private Sub TestPort(portName As String, label As String)
            ' Safe connectivity check:
            ' - Open port
            ' - Query lock status and sensor status for relayId 1
            ' - Close port
            Try
                StatusText.Text = $"Testing Branch {label} on {portName}…"

                Dim board As New HldLockBoard()
                board.Open(portName, HldBaudRate)

                ' Probe relayId = 1 (safe: just a read)
                Dim lockStatus As Integer = board.GetLockStatus(1)
                Dim sensorStatus As Integer = board.GetSensorStatus(1)

                board.Close()

                StatusText.Text = $"OK: Branch {label} {portName}  LockStatus(1)={lockStatus}  SensorStatus(1)={sensorStatus}"
            Catch ex As Exception
                StatusText.Text = $"FAIL: Branch {label} {portName}  {ex.Message}"
            End Try
        End Sub

    End Class

End Namespace

