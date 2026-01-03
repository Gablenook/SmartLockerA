Imports System.Collections.ObjectModel
Imports System.Text
Imports System.Linq
Imports Microsoft.EntityFrameworkCore
Imports System.Windows
Imports System.Windows.Controls

Partial Public Class LockerStatusAdmin
    Inherits Window

    Public Property AdminActorId As String = "Admin:Unknown"

    Private ReadOnly _rows As New ObservableCollection(Of LockerStatusRow)
    Private _isScanning As Boolean = False

    Public Sub New()
        InitializeComponent()

        GridStatus.ItemsSource = _rows
        InitializeComboSources()

        LoadFromDb()
    End Sub
    Private Sub InitializeComboSources()
        ' Populate ComboBox sources for DataGridComboBoxColumns (Occupancy, Lock)
        Dim occupancyValues = [Enum].GetValues(GetType(OccupancyState)).Cast(Of OccupancyState)().ToList()
        Dim lockValues = [Enum].GetValues(GetType(LockState)).Cast(Of LockState)().ToList()

        ' Columns: 5=Occupancy, 6=Lock based on your XAML order
        Dim occCol = TryCast(GridStatus.Columns(5), DataGridComboBoxColumn)
        If occCol IsNot Nothing Then occCol.ItemsSource = occupancyValues

        Dim lockCol = TryCast(GridStatus.Columns(6), DataGridComboBoxColumn)
        If lockCol IsNot Nothing Then lockCol.ItemsSource = lockValues
    End Sub
    Private Sub SetUiBusy(isBusy As Boolean, status As String)
        ScanPortsButton.IsEnabled = Not isBusy
        ReloadButton.IsEnabled = Not isBusy
        SaveButton.IsEnabled = Not isBusy

        StatusText.Text = status
    End Sub
    Private Sub Reload_Click(sender As Object, e As RoutedEventArgs)
        LoadFromDb()
    End Sub
    Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
        Me.Close()
    End Sub
    Private Async Sub ScanPorts_Click(sender As Object, e As RoutedEventArgs)
        If _isScanning Then Return

        _isScanning = True
        SetUiBusy(True, "Scanning controller ports...")

        Try
            Dim ports As List(Of ControllerPort)

            ' IMPORTANT: Use your bootstrapper so options are provided
            Using db = DatabaseBootstrapper.BuildDbContext()
                ports = Await db.ControllerPorts.
                    AsNoTracking().
                    OrderBy(Function(p) p.BranchName). ' ControllerPort uses BranchName per your model
                    ThenBy(Function(p) p.PortName).
                    ToListAsync()
            End Using

            If ports Is Nothing OrElse ports.Count = 0 Then
                StatusText.Text = "No ControllerPorts configured."
                Return
            End If

            Dim results As List(Of PortScanResult) =
                Await Task.Run(Function() ScanPortsCore(ports))

            If results Is Nothing OrElse results.Count = 0 Then
                StatusText.Text = "Scan complete. No results returned."
                Return
            End If

            Dim okCount As Integer = results.Where(Function(x) x.Success).Count()
            Dim failCount As Integer = results.Where(Function(x) Not x.Success).Count()

            Dim report As String = BuildPortScanReport(results)

            StatusText.Text = $"Scan complete. {okCount} OK / {failCount} failed."
            MessageBox.Show(report, "Scan Ports", MessageBoxButton.OK, MessageBoxImage.Information)

        Catch ex As Exception
            StatusText.Text = "Scan failed."
            MessageBox.Show(ex.Message, "Scan Ports Error", MessageBoxButton.OK, MessageBoxImage.Error)

        Finally
            SetUiBusy(False, "Ready.")
            _isScanning = False
        End Try
    End Sub
    Private Function BuildPortScanReport(results As List(Of PortScanResult)) As String
        Dim sb As New StringBuilder()
        sb.AppendLine("Port scan results:")
        sb.AppendLine()

        For Each r In results
            sb.AppendLine($"{r.Branch}  {r.PortName}  =>  {If(r.Success, "OK", "FAIL")}  {r.Message}")
        Next

        Return sb.ToString()
    End Function
    Private Function ScanPortsCore(ports As List(Of ControllerPort)) As List(Of PortScanResult)
        Dim results As New List(Of PortScanResult)

        For Each p In ports
            Dim r As New PortScanResult With {
                .Branch = p.BranchName,     ' ControllerPort uses BranchName per your model
                .PortName = p.PortName
            }

            Try
                ' ======= PLUG IN YOUR DLL CALLS HERE =======
                ' Example:
                ' Using board As New VendorBoard()
                '     board.Open(p.PortName, p.BaudRate)
                '     ' optionally: query expected relay count or do a noop command
                '     board.Close()
                ' End Using

                r.Success = True
                r.Message = "Connected"

            Catch ex As Exception
                r.Success = False
                r.Message = ex.Message
            End Try

            results.Add(r)
        Next

        Return results
    End Function
    Private Sub LoadFromDb()
        Try
            _rows.Clear()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim lockers = db.Lockers.AsNoTracking().
                    OrderBy(Function(l) l.LockerNumber).
                    Select(Function(l) New With {
                        l.LockerId,
                        l.LockerNumber,
                        l.Branch,
                        l.RelayId,
                        l.SizeCode
                    }).
                    ToList()

                Dim statusByLockerId As Dictionary(Of Integer, LockerStatus) =
                    db.LockerStatuses.AsNoTracking().
                        ToList().
                        ToDictionary(Function(s) s.LockerId)

                For Each l In lockers
                    Dim s As LockerStatus = Nothing
                    statusByLockerId.TryGetValue(l.LockerId, s)

                    _rows.Add(New LockerStatusRow With {
                        .LockerId = l.LockerId,
                        .LockerNumber = l.LockerNumber,
                        .Branch = l.Branch,
                        .RelayId = l.RelayId,
                        .SizeCode = l.SizeCode,
                        .LockState = If(s Is Nothing, LockState.Unknown, s.LockState),
                        .OccupancyState = If(s Is Nothing, OccupancyState.Unknown, s.OccupancyState),
                        .PackagePresent = If(s Is Nothing, CType(Nothing, Boolean?), s.PackagePresent),
                        .ReservedUntilUtc = If(s Is Nothing, CType(Nothing, DateTime?), s.ReservedUntilUtc),
                        .ReservedWorkOrderNumber = If(s Is Nothing, Nothing, s.ReservedWorkOrderNumber),
                        .LastUpdatedUtc = If(s Is Nothing, DateTime.MinValue, s.LastUpdatedUtc),
                        .LastActorId = If(s Is Nothing, Nothing, s.LastActorId),
                        .Reason = "",
                        .IsDirty = False
                    })
                Next

            End Using

            StatusText.Text = $"Loaded {_rows.Count} locker(s)."

        Catch ex As Exception
            StatusText.Text = $"Load failed: {ex.Message}"
        End Try
    End Sub
    Private Sub Save_Click(sender As Object, e As RoutedEventArgs)

        GridStatus.CommitEdit(DataGridEditingUnit.Cell, True)
        GridStatus.CommitEdit(DataGridEditingUnit.Row, True)

        Dim dirty = _rows.Where(Function(r) r IsNot Nothing AndAlso r.IsDirty).ToList()
        If dirty.Count = 0 Then
            StatusText.Text = "No changes to save."
            Return
        End If

        Dim correlationId As String = Guid.NewGuid().ToString("N")
        Dim saved As Integer = 0
        Dim failed As Integer = 0

        Try
            Using db = DatabaseBootstrapper.BuildDbContext()

                For Each r In dirty
                    Try
                        Dim status = db.LockerStatuses.SingleOrDefault(Function(s) s.LockerId = r.LockerId)
                        If status Is Nothing Then
                            status = New LockerStatus With {.LockerId = r.LockerId}
                            db.LockerStatuses.Add(status)
                        End If

                        status.OccupancyState = r.OccupancyState
                        status.LockState = r.LockState
                        status.PackagePresent = r.PackagePresent
                        status.LastUpdatedUtc = DateTime.UtcNow
                        status.LastActorId = AdminActorId

                        Dim rsn = (If(r.Reason, "")).Trim()
                        If rsn.Length = 0 Then rsn = $"Admin set status: Occ={r.OccupancyState}, Lock={r.LockState}"
                        status.LastReason = rsn

                        If r.OccupancyState = OccupancyState.Unavailable OrElse r.OccupancyState = OccupancyState.OutOfService Then
                            status.ReservedUntilUtc = Nothing
                            status.ReservedCorrelationId = Nothing
                            status.ReservedWorkOrderNumber = Nothing
                        End If

                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                            .ActorType = Audit.ActorType.Admin,
                            .ActorId = AdminActorId,
                            .AffectedComponent = "LockerStatusAdmin",
                            .Outcome = Audit.AuditOutcome.Success,
                            .CorrelationId = correlationId,
                            .ReasonCode = $"Locker={r.LockerNumber};Occ={r.OccupancyState};Lock={r.LockState}"
                        })

                        saved += 1
                        r.IsDirty = False

                    Catch exRow As Exception
                        failed += 1

                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                            .ActorType = Audit.ActorType.Admin,
                            .ActorId = AdminActorId,
                            .AffectedComponent = "LockerStatusAdmin",
                            .Outcome = Audit.AuditOutcome.Error,
                            .CorrelationId = correlationId,
                            .ReasonCode = $"LockerId={r.LockerId};SaveFailed"
                        })
                    End Try
                Next

                db.SaveChanges()
            End Using

            StatusText.Text = $"Saved {saved} change(s). Failed {failed}."
            LoadFromDb()

        Catch ex As Exception
            StatusText.Text = $"Save failed: {ex.Message}"
        End Try

    End Sub

End Class


