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
    Private Async Function SaveAdminLockerStatusChangeAsync(row As LockerStatusRow) As Task(Of LockerActionResult)

        If row Is Nothing Then Throw New ArgumentNullException(NameOf(row))

        Dim correlationId As String = Guid.NewGuid().ToString("N")
        Dim actionService As New LockerActionService()

        Dim rsn = If(row.Reason, "").Trim()

        If rsn.Length = 0 Then
            rsn = $"Admin set status: Occ={row.OccupancyState}, Lock={row.LockState}"
        End If

        Dim request As New LockerActionRequest With {
        .Workflow = "admin",
        .ActionType = "AdminSaveLockerStatus",
        .LockerId = row.LockerId,
        .LockerNumber = row.LockerNumber,
        .Branch = row.Branch,
        .RelayId = row.RelayId,
        .ActorId = If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
        .AssetTag = row.CurrentAssetTag,
        .DeviceType = row.CurrentDeviceType,
        .CorrelationId = correlationId,
        .RequiresBackendAck = False
    }

        Dim result = Await actionService.ExecuteAdminStateChangeAsync(
        request,
        Function()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim status = db.LockerStatuses.
                    SingleOrDefault(Function(s) s.LockerId = row.LockerId)

                If status Is Nothing Then
                    status = New LockerStatus With {.LockerId = row.LockerId}
                    db.LockerStatuses.Add(status)
                End If

                status.OccupancyState = row.OccupancyState
                status.LockState = row.LockState
                status.PackagePresent = row.PackagePresent
                status.LastUpdatedUtc = DateTime.UtcNow
                status.LastActorId = request.ActorId
                status.LastReason = rsn

                If row.OccupancyState = OccupancyState.Unavailable OrElse
                   row.OccupancyState = OccupancyState.OutOfService Then

                    status.ReservedUntilUtc = Nothing
                    status.ReservedCorrelationId = Nothing
                    status.ReservedWorkOrderNumber = Nothing

                End If

                db.SaveChanges()

            End Using

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = request.ActorId,
                .AffectedComponent = "LockerStatusAdmin",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId,
                .ReasonCode = $"AdminSaveLockerStatus;Locker={row.LockerNumber};Occ={row.OccupancyState};Lock={row.LockState}"
            })

            Return Task.CompletedTask

        End Function)

        Return result

    End Function
    Private Async Sub ResetLocker_Click(sender As Object, e As RoutedEventArgs)

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
            SetUiBusy(True, $"Resetting locker {selected.LockerNumber}...")

            Dim result = Await ResetLockerForTestingAsync(selected)

            If result.Success Then
                LoadFromDb()
                StatusText.Text = $"Locker {selected.LockerNumber} reset. JournalId={result.JournalId}."
            Else
                StatusText.Text = $"Reset failed: {result.ErrorMessage}"
                MessageBox.Show(result.ErrorMessage, "Reset Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End If

        Catch ex As Exception
            StatusText.Text = $"Reset failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Reset Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)

        Finally
            SetUiBusy(False, "Ready.")
        End Try

    End Sub
    Private Async Function ResetLockerForTestingAsync(row As LockerStatusRow) As Task(Of LockerActionResult)

        If row Is Nothing Then Throw New ArgumentNullException(NameOf(row))

        Dim correlationId As String = Guid.NewGuid().ToString("N")
        Dim actionService As New LockerActionService()

        Dim request As New LockerActionRequest With {
        .Workflow = "admin",
        .ActionType = "AdminResetLockerForTesting",
        .LockerId = row.LockerId,
        .LockerNumber = row.LockerNumber,
        .Branch = row.Branch,
        .RelayId = row.RelayId,
        .ActorId = If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
        .CorrelationId = correlationId,
        .RequiresBackendAck = False
    }

        Dim result = Await actionService.ExecuteAdminStateChangeAsync(
        request,
        Function()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim status = db.LockerStatuses.
                    SingleOrDefault(Function(s) s.LockerId = row.LockerId)

                If status Is Nothing Then
                    status = New LockerStatus With {.LockerId = row.LockerId}
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
                status.DefectType = Nothing

                status.LastWorkOrderNumber = Nothing
                status.LastActorId = request.ActorId
                status.LastReason = "Admin reset locker for testing"
                status.LastUpdatedUtc = DateTime.UtcNow

                db.SaveChanges()

            End Using

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = request.ActorId,
                .AffectedComponent = "LockerStatusAdmin",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId,
                .ReasonCode = $"ResetLockerForTesting;Locker={row.LockerNumber};LockerId={row.LockerId}"
            })

            Return Task.CompletedTask

        End Function)

        Return result

    End Function
    Private Async Sub OpenLocker_Click(sender As Object, e As RoutedEventArgs)

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
            SetUiBusy(True, $"Opening locker {selected.LockerNumber}...")

            Dim result = Await OpenLockerForAdminAsync(selected)

            If result.Success Then
                StatusText.Text =
                $"Open command sent for locker {selected.LockerNumber}. JournalId={result.JournalId}."
            Else
                StatusText.Text = $"Open locker failed: {result.ErrorMessage}"
                MessageBox.Show(result.ErrorMessage, "Open Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End If

        Catch ex As Exception
            StatusText.Text = $"Open locker failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Open Locker Error", MessageBoxButton.OK, MessageBoxImage.Error)

        Finally
            SetUiBusy(False, "Ready.")
        End Try

    End Sub
    Private Async Function OpenLockerForAdminAsync(row As LockerStatusRow) As Task(Of LockerActionResult)

        If row Is Nothing Then
            Throw New ArgumentNullException(NameOf(row))
        End If

        If LockerController Is Nothing Then
            Throw New InvalidOperationException("Locker controller service is not available.")
        End If

        Dim correlationId As String = Guid.NewGuid().ToString("N")

        Dim actionService As New LockerActionService()

        Dim request As New LockerActionRequest With {
        .Workflow = "admin",
        .ActionType = "AdminOpenLocker",
        .LockerId = row.LockerId,
        .LockerNumber = row.LockerNumber,
        .Branch = row.Branch,
        .RelayId = row.RelayId,
        .ActorId = If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
        .CorrelationId = correlationId,
        .RequiresBackendAck = False
    }

        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
        .EventType = Audit.AuditEventType.LockerOpenAttempt,
        .ActorType = Audit.ActorType.Admin,
        .ActorId = request.ActorId,
        .AffectedComponent = "LockerStatusAdmin",
        .Outcome = Audit.AuditOutcome.Success,
        .CorrelationId = correlationId,
        .ReasonCode = $"AdminOpenRequested;Locker={row.LockerNumber}"
    })

        Dim result = Await actionService.ExecuteLockerActionAsync(
        request,
        Function()

            Dim opened = LockerController.UnlockByLockerNumber(row.LockerNumber)

            If Not opened Then
                Throw New InvalidOperationException(
                    $"Unlock command was not accepted for locker {row.LockerNumber}.")
            End If

            Return Task.CompletedTask

        End Function)

        If result.Success Then
            Await actionService.UpdateJournalStateAsync(
            result.JournalId,
            LockerTransactionState.AckSucceeded,
            LockerAckStatus.NotRequired)
        End If

        Return result

    End Function
    Private Sub Window_KeyDown(sender As Object, e As KeyEventArgs)

        If e.Key = Key.Escape Then
            Me.Close()
        End If

    End Sub
    Private Async Sub ClearDefective_Click(sender As Object, e As RoutedEventArgs)

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
            SetUiBusy(True, $"Clearing defective hold for locker {selected.LockerNumber}...")

            Dim result = Await ClearDefectiveHoldForLockerAsync(selected)

            If result.Success Then
                LoadFromDb()
                StatusText.Text = $"Defective hold cleared for locker {selected.LockerNumber}. JournalId={result.JournalId}."
            Else
                StatusText.Text = $"Clear defective failed: {result.ErrorMessage}"
                MessageBox.Show(result.ErrorMessage, "Clear Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End If

        Catch ex As Exception
            StatusText.Text = $"Clear defective failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Clear Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)

        Finally
            SetUiBusy(False, "Ready.")
        End Try

    End Sub
    Private Async Function ClearDefectiveHoldForLockerAsync(row As LockerStatusRow) As Task(Of LockerActionResult)

        If row Is Nothing Then Throw New ArgumentNullException(NameOf(row))

        Dim correlationId As String = Guid.NewGuid().ToString("N")
        Dim actionService As New LockerActionService()

        Dim request As New LockerActionRequest With {
        .Workflow = "admin",
        .ActionType = "AdminClearDefectiveHold",
        .LockerId = row.LockerId,
        .LockerNumber = row.LockerNumber,
        .Branch = row.Branch,
        .RelayId = row.RelayId,
        .ActorId = If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
        .AssetTag = row.CurrentAssetTag,
        .DeviceType = row.CurrentDeviceType,
        .CorrelationId = correlationId,
        .RequiresBackendAck = False
    }

        Dim result = Await actionService.ExecuteAdminStateChangeAsync(
        request,
        Function()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim status = db.LockerStatuses.
                    SingleOrDefault(Function(s) s.LockerId = row.LockerId)

                If status Is Nothing Then
                    Throw New InvalidOperationException(
                        $"No LockerStatus row found for locker {row.LockerNumber}.")
                End If

                status.IsDefectiveHold = False
                status.LastActorId = request.ActorId
                status.LastReason = "Admin cleared defective hold"
                status.LastUpdatedUtc = DateTime.UtcNow

                db.SaveChanges()

            End Using

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = request.ActorId,
                .AffectedComponent = "LockerStatusAdmin",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId,
                .ReasonCode = $"ClearDefectiveHold;Locker={row.LockerNumber};LockerId={row.LockerId}"
            })

            Return Task.CompletedTask

        End Function)

        Return result

    End Function
    Private Async Sub MarkDefective_Click(sender As Object, e As RoutedEventArgs)

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
            SetUiBusy(True, $"Marking locker {selected.LockerNumber} defective...")

            Dim result = Await MarkDefectiveHoldForLockerAsync(selected)

            If result.Success Then
                LoadFromDb()
                StatusText.Text = $"Locker {selected.LockerNumber} marked defective. JournalId={result.JournalId}."
            Else
                StatusText.Text = $"Mark defective failed: {result.ErrorMessage}"
                MessageBox.Show(result.ErrorMessage, "Mark Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End If

        Catch ex As Exception
            StatusText.Text = $"Mark defective failed: {ex.Message}"
            MessageBox.Show(ex.Message, "Mark Defective Error", MessageBoxButton.OK, MessageBoxImage.Error)

        Finally
            SetUiBusy(False, "Ready.")
        End Try

    End Sub
    Private Async Function MarkDefectiveHoldForLockerAsync(row As LockerStatusRow) As Task(Of LockerActionResult)

        If row Is Nothing Then Throw New ArgumentNullException(NameOf(row))

        Dim correlationId As String = Guid.NewGuid().ToString("N")
        Dim actionService As New LockerActionService()

        Dim request As New LockerActionRequest With {
        .Workflow = "admin",
        .ActionType = "AdminMarkDefectiveHold",
        .LockerId = row.LockerId,
        .LockerNumber = row.LockerNumber,
        .Branch = row.Branch,
        .RelayId = row.RelayId,
        .ActorId = If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
        .AssetTag = row.CurrentAssetTag,
        .DeviceType = row.CurrentDeviceType,
        .CorrelationId = correlationId,
        .RequiresBackendAck = False
    }

        Dim result = Await actionService.ExecuteAdminStateChangeAsync(
        request,
        Function()

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim status = db.LockerStatuses.
                    SingleOrDefault(Function(s) s.LockerId = row.LockerId)

                If status Is Nothing Then
                    Throw New InvalidOperationException(
                        $"No LockerStatus row found for locker {row.LockerNumber}.")
                End If

                status.IsDefectiveHold = True
                status.LastActorId = request.ActorId
                status.LastReason = "Admin marked defective hold"
                status.LastUpdatedUtc = DateTime.UtcNow

                db.SaveChanges()

            End Using

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.PolicyConfigurationChange,
                .ActorType = Audit.ActorType.Admin,
                .ActorId = request.ActorId,
                .AffectedComponent = "LockerStatusAdmin",
                .Outcome = Audit.AuditOutcome.Success,
                .CorrelationId = correlationId,
                .ReasonCode = $"MarkDefectiveHold;Locker={row.LockerNumber};LockerId={row.LockerId}"
            })

            Return Task.CompletedTask

        End Function)

        Return result

    End Function
    Private Async Sub Save_Click(sender As Object, e As RoutedEventArgs)

        Dim row = TryCast(Me.DataContext, LockerStatusRow)

        If row Is Nothing Then
            row = TryCast(TryCast(sender, FrameworkElement)?.DataContext, LockerStatusRow)
        End If

        If row Is Nothing Then
            MessageBox.Show("Select a locker row before saving.", "Save Locker Status")
            Return
        End If

        Try
            Dim result = Await SaveAdminLockerStatusChangeAsync(row)

            If result IsNot Nothing AndAlso result.Success Then
                MessageBox.Show("Locker status saved.", "Save Locker Status")
            Else
                Dim msg = If(result?.Message, "Locker status save failed.")
                MessageBox.Show(msg, "Save Locker Status")
            End If

        Catch ex As Exception
            MessageBox.Show(ex.Message, "Save Locker Status Error")
        End Try

    End Sub
End Class

