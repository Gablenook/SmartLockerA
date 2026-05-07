Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Text
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports Microsoft.EntityFrameworkCore

Partial Public Class LockerStatusAdmin
    Inherits Window

    Public Property ActorId As String = "Admin:Unknown"
    Public Property LockerController As LockerControllerService

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
        ResetLockerButton.IsEnabled = Not isBusy
        OpenLockerButton.IsEnabled = Not isBusy
        ClearDefectiveButton.IsEnabled = Not isBusy
        MarkDefectiveButton.IsEnabled = Not isBusy
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
                        .CurrentAssetTag = If(s Is Nothing, Nothing, s.CurrentAssetTag),
                        .CurrentDeviceType = If(s Is Nothing, Nothing, s.CurrentDeviceType),
                        .IsDefectiveHold = If(s Is Nothing, False, s.IsDefectiveHold),
                        .DefectType = If(s Is Nothing, Nothing, s.DefectType),
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
                        status.LastActorId = ActorId

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
                            .ActorId = ActorId,
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
                            .ActorId = ActorId,
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
    Private Sub ResetLocker_Click(sender As Object, e As RoutedEventArgs)

        GridStatus.CommitEdit(DataGridEditingUnit.Cell, True)
        GridStatus.CommitEdit(DataGridEditingUnit.Row, True)

        Dim selected = TryCast(GridStatus.SelectedItem, LockerStatusRow)

        If selected Is Nothing Then
            MessageBox.Show(
                "Select one locker row first.",
                "Reset Locker",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
            Return
        End If

        Dim confirm = MessageBox.Show(
            $"Reset locker {selected.LockerNumber} to vacant/available for testing?" & Environment.NewLine &
            Environment.NewLine &
            "This will clear reservation, occupancy, asset assignment, package-present status, and defective hold.",
            "Confirm Locker Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning)

        If confirm <> MessageBoxResult.Yes Then Return

        Try
            ResetLockerForTesting(selected.LockerId, selected.LockerNumber)
            LoadFromDb()
            StatusText.Text = $"Locker {selected.LockerNumber} reset."

        Catch ex As Exception
            StatusText.Text = $"Reset failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Reset Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try

    End Sub
    Private Sub ResetLockerForTesting(lockerId As Integer, lockerNumber As String)

        Dim correlationId As String = Guid.NewGuid().ToString("N")

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim status = db.LockerStatuses.
                SingleOrDefault(Function(s) s.LockerId = lockerId)

            If status Is Nothing Then
                status = New LockerStatus With {.LockerId = lockerId}
                db.LockerStatuses.Add(status)
            End If

            status.OccupancyState = OccupancyState.Vacant
            status.LockState = LockState.Closed
            status.PackagePresent = Nothing

            status.ReservedUntilUtc = Nothing
            status.ReservedCorrelationId = Nothing
            status.ReservedWorkOrderNumber = Nothing

            status.CurrentAssetTag = Nothing
            status.CurrentDeviceType = Nothing
            status.IsDefectiveHold = False

            status.LastWorkOrderNumber = Nothing
            status.LastActorId = ActorId
            status.LastReason = "Admin reset locker for testing"
            status.LastUpdatedUtc = DateTime.UtcNow

            db.SaveChanges()

        End Using

        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = ActorId,
            .AffectedComponent = "LockerStatusAdmin",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = correlationId,
            .ReasonCode = $"ResetLockerForTesting;Locker={lockerNumber};LockerId={lockerId}"
        })

    End Sub
    Private Sub OpenLocker_Click(sender As Object, e As RoutedEventArgs)

        Dim selected = TryCast(GridStatus.SelectedItem, LockerStatusRow)

        If selected Is Nothing Then
            MessageBox.Show(
                "Select one locker row first.",
                "Open Locker",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
            Return
        End If

        Dim confirm = MessageBox.Show(
            $"Open locker {selected.LockerNumber}?" & Environment.NewLine &
            Environment.NewLine &
            "Use this only for admin/service recovery.",
            "Confirm Open Locker",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning)

        If confirm <> MessageBoxResult.Yes Then Return

        Try
            OpenLockerForAdmin(selected)
            StatusText.Text = $"Open command sent for locker {selected.LockerNumber}."

        Catch ex As Exception
            StatusText.Text = $"Open locker failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Open Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try

    End Sub
    Private Sub OpenLockerForAdmin(row As LockerStatusRow)

        If row Is Nothing Then
            Throw New ArgumentNullException(NameOf(row))
        End If

        Dim correlationId As String = Guid.NewGuid().ToString("N")

        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.LockerOpenAttempt,
        .ActorType = Audit.ActorType.Admin,
        .ActorId = ActorId,
        .AffectedComponent = "LockerStatusAdmin",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = correlationId,
        .ReasonCode = $"AdminOpenRequested;Locker={row.LockerNumber}"
    })

        If LockerController Is Nothing Then
            Throw New InvalidOperationException("Locker controller service is not available.")
        End If

        Dim opened = LockerController.UnlockByLockerNumber(row.LockerNumber)

        If Not opened Then
            Throw New InvalidOperationException(
            $"Unlock command was not accepted for locker {row.LockerNumber}.")
        End If

    End Sub
    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)

        If e.Key = Key.Escape Then
            Me.Close()
        End If

    End Sub
    Private Sub ClearDefective_Click(sender As Object, e As RoutedEventArgs)

        Dim selected = TryCast(GridStatus.SelectedItem, LockerStatusRow)

        If selected Is Nothing Then
            MessageBox.Show(
                "Select one locker row first.",
                "Clear Defective",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
            Return
        End If

        If Not selected.IsDefectiveHold Then
            MessageBox.Show(
                $"Locker {selected.LockerNumber} is not currently marked defective.",
                "Clear Defective",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
            Return
        End If

        Dim confirm = MessageBox.Show(
            $"Clear defective hold for locker {selected.LockerNumber}?" & Environment.NewLine &
            Environment.NewLine &
            $"Asset: {selected.CurrentAssetTag}" & Environment.NewLine &
            $"Device Type: {selected.CurrentDeviceType}",
            "Confirm Clear Defective",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning)

        If confirm <> MessageBoxResult.Yes Then Return

        Try
            ClearDefectiveHoldForLocker(selected.LockerId, selected.LockerNumber)
            LoadFromDb()
            StatusText.Text = $"Defective hold cleared for locker {selected.LockerNumber}."

        Catch ex As Exception
            StatusText.Text = $"Clear defective failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Clear Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try

    End Sub
    Private Sub ClearDefectiveHoldForLocker(lockerId As Integer, lockerNumber As String)

        Dim correlationId As String = Guid.NewGuid().ToString("N")

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim status = db.LockerStatuses.
                SingleOrDefault(Function(s) s.LockerId = lockerId)

            If status Is Nothing Then
                Throw New InvalidOperationException($"No LockerStatus row found for locker {lockerNumber}.")
            End If

            status.IsDefectiveHold = False
            status.LastActorId = ActorId
            status.LastReason = "Admin cleared defective hold"
            status.LastUpdatedUtc = DateTime.UtcNow

            db.SaveChanges()

        End Using

        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = ActorId,
            .AffectedComponent = "LockerStatusAdmin",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = correlationId,
            .ReasonCode = $"ClearDefectiveHold;Locker={lockerNumber};LockerId={lockerId}"
        })

    End Sub
    Private Sub MarkDefective_Click(sender As Object, e As RoutedEventArgs)

        Dim selected = TryCast(GridStatus.SelectedItem, LockerStatusRow)

        If selected Is Nothing Then
            MessageBox.Show(
                "Select one locker row first.",
                "Mark Defective",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
            Return
        End If

        Dim confirm = MessageBox.Show(
            $"Mark locker {selected.LockerNumber} as defective?" & Environment.NewLine &
            Environment.NewLine &
            $"Asset: {If(String.IsNullOrWhiteSpace(selected.CurrentAssetTag), "(none)", selected.CurrentAssetTag)}" & Environment.NewLine &
            $"Device Type: {If(String.IsNullOrWhiteSpace(selected.CurrentDeviceType), "(none)", selected.CurrentDeviceType)}",
            "Confirm Mark Defective",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning)

        If confirm <> MessageBoxResult.Yes Then Return

        Try
            MarkDefectiveHoldForLocker(selected.LockerId, selected.LockerNumber)
            LoadFromDb()
            StatusText.Text = $"Locker {selected.LockerNumber} marked defective."

        Catch ex As Exception
            StatusText.Text = $"Mark defective failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Mark Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try

    End Sub
    Private Sub MarkDefectiveHoldForLocker(lockerId As Integer, lockerNumber As String)

        Dim correlationId As String = Guid.NewGuid().ToString("N")

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim status = db.LockerStatuses.
                SingleOrDefault(Function(s) s.LockerId = lockerId)

            If status Is Nothing Then
                Throw New InvalidOperationException(
                    $"No LockerStatus row found for locker {lockerNumber}.")
            End If

            status.IsDefectiveHold = True
            status.LastActorId = ActorId
            status.LastReason = "Admin marked defective hold"
            status.LastUpdatedUtc = DateTime.UtcNow

            db.SaveChanges()

        End Using

        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = ActorId,
            .AffectedComponent = "LockerStatusAdmin",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = correlationId,
            .ReasonCode = $"MarkDefectiveHold;Locker={lockerNumber};LockerId={lockerId}"
        })

    End Sub
End Class

