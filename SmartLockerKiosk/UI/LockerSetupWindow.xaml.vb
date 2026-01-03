Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Windows
Imports System.Windows.Data
Imports System.Windows.Input
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Namespace SmartLockerKiosk
    Partial Public Class LockerSetupWindow

        ' The grid edits THIS collection only
        Private ReadOnly _rows As New ObservableCollection(Of LockerRow)
        Public Property AdminActorID As String
        Private ReadOnly _scanRows As New ObservableCollection(Of RelayScanRow)
        Private _isScanning As Boolean = False
        ' ---------- Live Monitor state ----------
        Private ReadOnly _liveRows As New ObservableCollection(Of LiveMonitorRow)
        Private _liveCts As System.Threading.CancellationTokenSource = Nothing
        Private _liveTask As Task = Nothing
        Private _liveBoard As HldLockBoard = Nothing
        Private _liveStartedUtc As DateTime = DateTime.MinValue

        Public Sub New()
            InitializeComponent()
            ' Bind live monitor grid
            LiveMonitorGrid.ItemsSource = _liveRows
            SeedLiveRows() ' create rows 1..8 once


            ' Bind once; never rebind to a different collection
            LockersGrid.ItemsSource = _rows
            StatusText.Text = "Loading lockers..."
            LoadLockersFromDb()
        End Sub
        Private Sub LockerSetupWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            'RunMappingDiagnostics()
            'RunSingleDiagnostic()
        End Sub
        Private Sub Load_Click(sender As Object, e As RoutedEventArgs)
            LoadLockersFromDb()
        End Sub
        Private Sub Add_Click(sender As Object, e As RoutedEventArgs)

            Dim defaultSizeCode As String = "A" ' safe fallback

            Try
                Using db = DatabaseBootstrapper.BuildDbContext()

                    ' If you have LockerSizes in your context, use it.
                    ' If you named it differently, adjust db.LockerSizes accordingly.
                    Dim firstEnabled =
                db.LockerSizes.
                   AsNoTracking().
                   Where(Function(s) s.IsEnabled).
                   OrderBy(Function(s) s.SortOrder).
                   Select(Function(s) s.SizeCode).
                   FirstOrDefault()

                    If Not String.IsNullOrWhiteSpace(firstEnabled) Then
                        defaultSizeCode = firstEnabled.Trim().ToUpperInvariant()
                    End If
                End Using
            Catch
                ' If LockerSizes table/db set isn't present yet, ignore and keep "A"
            End Try

            _rows.Add(New LockerRow With {
        .LockerId = 0,
        .Branch = "A",
        .RelayId = 1,
        .LockerNumber = NextSuggestedLockerNumber(),
        .SizeCode = defaultSizeCode,
        .Zone = "",
        .IsEnabled = True
    })
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = AdminActorID,
    .AffectedComponent = "LockerSetupWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = actionId,
    .ReasonCode = "LockerRowAdded"
})



            StatusText.Text = "Added a new locker row. Set Branch, RelayId, LockerNumber, then Save."

        End Sub
        Private Sub Remove_Click(sender As Object, e As RoutedEventArgs)
            Dim selected = LockersGrid.SelectedItems.Cast(Of LockerRow).ToList()
            If selected.Count = 0 Then
                StatusText.Text = "No rows selected."
                Return
            End If

            For Each r In selected
                _rows.Remove(r)
            Next

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = AdminActorID,
    .AffectedComponent = "LockerSetupWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = actionId,
    .ReasonCode = $"LockerRowsRemoved:Count={selected.Count}"
})


            StatusText.Text = $"Removed {selected.Count} row(s) from the grid. Click Save to commit."
        End Sub
        Private Sub Save_Click(sender As Object, e As RoutedEventArgs)

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' 1) Commit any in-progress edits so values land in _rows
            LockersGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, True)
            LockersGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, True)

            Dim view = CollectionViewSource.GetDefaultView(LockersGrid.ItemsSource)
            Dim editable = TryCast(view, IEditableCollectionView)
            If editable IsNot Nothing Then
                If editable.IsEditingItem Then editable.CommitEdit()
                If editable.IsAddingNew Then editable.CommitNew()
            End If

            ' 2) Validate
            Dim err = ValidateRows()
            If err IsNot Nothing Then
                MessageBox.Show(err)

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveDenied:ValidationFailed"
        })
                Return
            End If

            If _rows.Count = 0 Then
                MessageBox.Show("Nothing to save.")

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveDenied:Empty"
        })
                Return
            End If

            Try
                Dim addedCount As Integer = 0
                Dim updatedCount As Integer = 0
                Dim deletedCount As Integer = 0

                Using db = DatabaseBootstrapper.BuildDbContext()

                    ' Existing lockers keyed by id
                    Dim existing = db.Lockers.ToList().ToDictionary(Function(l) l.LockerId)

                    ' IDs present in grid
                    Dim idsInGrid As HashSet(Of Integer) =
                _rows.Where(Function(r) r IsNot Nothing AndAlso r.LockerId > 0).
                      Select(Function(r) r.LockerId).
                      ToHashSet()

                    ' Delete those removed from grid
                    If existing.Count > 0 Then
                        Dim toDelete = existing.Values.
                    Where(Function(l) Not idsInGrid.Contains(l.LockerId)).
                    ToList()

                        deletedCount = toDelete.Count
                        If deletedCount > 0 Then
                            db.Lockers.RemoveRange(toDelete)
                        End If
                    End If

                    ' Upsert rows
                    For Each r In _rows
                        If r Is Nothing Then Continue For

                        Dim entity As Locker = Nothing
                        Dim isNew As Boolean = (r.LockerId <= 0 OrElse Not existing.TryGetValue(r.LockerId, entity))

                        If isNew Then
                            entity = New Locker()
                            db.Lockers.Add(entity)
                            addedCount += 1
                        Else
                            ' Count as update if any meaningful field differs
                            Dim nb = NormalizeBranch(r.Branch)
                            Dim nln = NormalizeLockerNumber(r.LockerNumber)
                            Dim nsc = NormalizeSizeCode(r.SizeCode)
                            Dim nz = If(r.Zone, "")
                            Dim nen = r.IsEnabled

                            If Not String.Equals(entity.Branch, nb, StringComparison.OrdinalIgnoreCase) OrElse
                       entity.RelayId <> r.RelayId OrElse
                       Not String.Equals(entity.LockerNumber, nln, StringComparison.OrdinalIgnoreCase) OrElse
                       Not String.Equals(entity.SizeCode, nsc, StringComparison.OrdinalIgnoreCase) OrElse
                       Not String.Equals(If(entity.Zone, ""), nz, StringComparison.OrdinalIgnoreCase) OrElse
                       entity.IsEnabled <> nen Then
                                updatedCount += 1
                            End If
                        End If

                        entity.Branch = NormalizeBranch(r.Branch)
                        entity.RelayId = r.RelayId
                        entity.LockerNumber = NormalizeLockerNumber(r.LockerNumber)
                        entity.SizeCode = NormalizeSizeCode(r.SizeCode)
                        entity.Zone = If(r.Zone, "")
                        entity.IsEnabled = r.IsEnabled
                    Next

                    db.SaveChanges()
                End Using

                LoadLockersFromDb()
                StatusText.Text = "Saved."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"LockerConfigSaved:Added={addedCount};Updated={updatedCount};Deleted={deletedCount}"
        })

            Catch
                MessageBox.Show("Save failed.")

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveFailed"
        })
            End Try

        End Sub
        Private Async Sub ScanForLockers_Click(sender As Object, e As RoutedEventArgs)
            If _isScanning Then Return
            _isScanning = True

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Try
                ScanForLockersButton.IsEnabled = False
                AutoNumberButton.IsEnabled = False
                CommitDiscoveredButton.IsEnabled = False

                StatusText.Text = "Scanning controllers for relays..."

                ' 1) Load enabled ports once
                Dim ports As List(Of ControllerPort)
                Using db = DatabaseBootstrapper.BuildDbContext()
                    ports = Await db.ControllerPorts.AsNoTracking().
                Where(Function(p) p.IsEnabled AndAlso
                                  Not String.IsNullOrWhiteSpace(p.BranchName) AndAlso
                                  Not String.IsNullOrWhiteSpace(p.PortName)).
                OrderBy(Function(p) p.BranchName).
                ThenBy(Function(p) p.PortName).
                ToListAsync()
                End Using

                If ports Is Nothing OrElse ports.Count = 0 Then
                    StatusText.Text = "No enabled ControllerPorts configured."
                    Return
                End If

                ' 2) Run scan (KEEP ON SAME THREAD - matches DiagnosticProbePort behavior)
                Dim portResults As List(Of PortScanResult) = ScanControllersForRelaysCore(ports)

                ' 3) Flatten results to _scanRows
                _scanRows.Clear()
                For Each pr In portResults
                    If pr Is Nothing OrElse pr.Rows Is Nothing Then Continue For
                    For Each rr In pr.Rows
                        _scanRows.Add(rr)
                    Next
                Next

                Dim okRelays As Integer =
    _scanRows.Where(Function(r) r IsNot Nothing AndAlso r.Success).Count()

                Dim failRelays As Integer =
    _scanRows.Where(Function(r) r IsNot Nothing AndAlso Not r.Success).Count()


                StatusText.Text = $"Scan complete. Relays OK={okRelays}, Fail={failRelays}."

                ' 4) Build + save report
                Dim report As String = BuildProbeReport(_scanRows)

                Dim path As String = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"RelayProbe_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        )

                System.IO.File.WriteAllText(path, report)

                MessageBox.Show($"Probe report saved to:{Environment.NewLine}{path}",
                        "Relay Probe",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information)

                ' Enable next steps
                CommitDiscoveredButton.IsEnabled = (okRelays > 0)
                AutoNumberButton.IsEnabled = False

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"CommissionScan:OkRelays={okRelays};FailRelays={failRelays}"
        })

            Catch ex As Exception
                StatusText.Text = $"Scan failed: {ex.Message}"

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "CommissionScanFailed"
        })

            Finally
                ScanForLockersButton.IsEnabled = True
                _isScanning = False
            End Try
        End Sub

        Private Sub CommitDiscovered_Click(sender As Object, e As RoutedEventArgs)
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Try
                If _scanRows Is Nothing OrElse _scanRows.Count = 0 Then
                    StatusText.Text = "Nothing to commit. Run Scan For Lockers first."
                    Return
                End If

                Dim discovered = _scanRows.
            Where(Function(r) r.Success).
            Select(Function(r) (r.Branch.Trim().ToUpperInvariant(), r.RelayId)).
            Distinct().
            ToList()

                If discovered.Count = 0 Then
                    StatusText.Text = "No successful relay reads to commit."
                    Return
                End If

                UpsertDiscoveredLockers(discovered)

                ' After committing, reload the grid so the user sees new lockers
                LoadLockersFromDb()

                ' Now auto-number can run (it relies on DB having these lockers)
                AutoNumberButton.IsEnabled = True
                CommitDiscoveredButton.IsEnabled = False

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"CommissionCommit:Count={discovered.Count}"
        })

            Catch ex As Exception
                StatusText.Text = $"Commit failed: {ex.Message}"

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "CommissionCommitFailed"
        })
            End Try
        End Sub
        Private Sub AutoNumber_Click(sender As Object, e As RoutedEventArgs)
            ' Stub for now—next step will be the unlock + close-in-sequence routine
            MessageBox.Show("Auto Number is not implemented yet. Next step is: unlock all, then assign LockerNumber in close order.", "Auto Number", MessageBoxButton.OK, MessageBoxImage.Information)
        End Sub
        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub
        Private Sub TextBox_GotFocus(sender As Object, e As RoutedEventArgs)
            KeyboardHelper.ShowTouchKeyboard()
        End Sub
        Private Sub LockerSetupWindow_Closed(sender As Object, e As EventArgs) Handles Me.Closed
            KeyboardHelper.HideTouchKeyboard()
            Try
                If _liveCts IsNot Nothing Then _liveCts.Cancel()
            Catch
            End Try
        End Sub
        ' ---------- DB Load ----------
        Private Sub LoadLockersFromDb()

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Try
                _rows.Clear()

                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim lockers = db.Lockers.AsNoTracking().
                OrderBy(Function(x) x.LockerNumber).
                ToList()

                    For Each l In lockers
                        _rows.Add(New LockerRow With {
                    .LockerId = l.LockerId,
                    .Branch = l.Branch,
                    .RelayId = l.RelayId,
                    .LockerNumber = l.LockerNumber,
                    .SizeCode = l.SizeCode,
                    .Zone = l.Zone,
                    .IsEnabled = l.IsEnabled
                })
                    Next
                End Using

                StatusText.Text = $"Loaded {_rows.Count} locker(s)."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"LockerConfigLoaded:Count={_rows.Count}"
        })

            Catch
                StatusText.Text = "ERROR loading lockers."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigLoadFailed"
        })
            End Try

        End Sub
        Private Function ScanControllersForRelaysCore(ports As List(Of ControllerPort)) As List(Of PortScanResult)
            Dim results As New List(Of PortScanResult)

            If ports Is Nothing Then Return results

            For Each p In ports
                Dim branch As String = (If(p.BranchName, "")).Trim().ToUpperInvariant()
                Dim portName As String = (If(p.PortName, "")).Trim()

                Dim pr As New PortScanResult With {
            .Branch = branch,
            .PortName = portName,
            .Success = False,
            .Message = ""
        }

                Dim board As HldLockBoard = Nothing

                Try
                    board = New HldLockBoard()
                    board.Open(portName, 115200)
                    System.Threading.Thread.Sleep(200)

                    ' For NOW, use 16 for your bench test.
                    ' Later: switch this to detectedBoards*8, capped at 200.
                    Dim maxRelayToScan As Integer = 16

                    For relayId As Integer = 1 To maxRelayToScan
                        Dim row As New RelayScanRow With {
                    .Branch = branch,
                    .PortName = portName,
                    .RelayId = relayId,
                    .Success = False,
                    .UnlockedDuringProbe = False,
                    .ErrorMessage = Nothing,
                    .Samples = Nothing,
                    .SampleCount = 0
                }

                        Try
                            Dim probe = ProbeRelayWithTimeline(board, relayId,
                                                       samplesToTake:=20,          ' 20 * 120ms = 2.4s window
                                                       sampleIntervalMs:=120,      ' >=100ms query interval
                                                       settleAfterUnlockMs:=200,   ' like your diagnostic
                                                       unlockSpacingMs:=600,       ' >=600ms between unlocks
                                                       timeoutMs:=5000)            ' per SDK

                            row.LockStatus = probe.ls1
                            row.SensorStatus = probe.ss1
                            row.LockStatusAfter = probe.ls2
                            row.SensorStatusAfter = probe.ss2
                            row.UnlockedDuringProbe = True
                            row.Samples = probe.timeline
                            row.SampleCount = probe.sampleCount
                            row.Success = True

                        Catch exRow As Exception
                            row.Success = False
                            row.ErrorMessage = exRow.GetType().Name & ": " & exRow.Message
                        End Try

                        pr.Rows.Add(row)
                    Next

                    pr.ScannedRelays = pr.Rows.Count
                    pr.Success = True
                    pr.Message = $"Connected. RelaysScanned={pr.ScannedRelays}."

                Catch ex As Exception
                    pr.Success = False
                    pr.Message = ex.GetType().Name & ": " & ex.Message

                Finally
                    If board IsNot Nothing Then
                        Try : board.Close() : Catch : End Try
                    End If
                End Try

                results.Add(pr)
            Next

            Return results
        End Function

        Private Sub UpsertDiscoveredLockers(discovered As List(Of (Branch As String, RelayId As Integer)))
            If discovered Is Nothing OrElse discovered.Count = 0 Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                ' 1) Pull existing lockers for the discovered keys in ONE query
                Dim branches = discovered.Select(Function(x) x.Branch).Distinct().ToList()

                Dim existing = db.Lockers.
            Where(Function(l) branches.Contains(l.Branch)).
            ToList()

                ' Index existing by (Branch, RelayId)
                Dim existingByKey As New Dictionary(Of String, Locker)(StringComparer.OrdinalIgnoreCase)
                For Each l In existing
                    Dim key = $"{l.Branch.Trim().ToUpperInvariant()}|{l.RelayId}"
                    If Not existingByKey.ContainsKey(key) Then
                        existingByKey.Add(key, l)
                    End If
                Next

                Dim createdCount As Integer = 0
                Dim existingCount As Integer = 0

                ' 2) Create missing lockers
                For Each d In discovered
                    Dim b = d.Branch.Trim().ToUpperInvariant()
                    Dim key = $"{b}|{d.RelayId}"

                    If existingByKey.ContainsKey(key) Then
                        existingCount += 1
                        Continue For
                    End If

                    Dim newLocker As New Locker With {
                .Branch = b,
                .RelayId = d.RelayId,
                .LockerNumber = $"UNASSIGNED-{b}-{d.RelayId}",  ' temp unique value
                .SizeCode = Nothing,
                .Zone = Nothing
            }

                    db.Lockers.Add(newLocker)
                    existingByKey(key) = newLocker
                    createdCount += 1
                Next

                ' Save once so new lockers get their LockerId PKs
                db.SaveChanges()

                ' 3) Ensure LockerStatus exists for each discovered locker (one query)
                Dim lockerIds = existingByKey.Values.Select(Function(l) l.LockerId).ToList()

                Dim statusByLockerId As Dictionary(Of Integer, LockerStatus) =
            db.LockerStatuses.
                Where(Function(s) lockerIds.Contains(s.LockerId)).
                ToList().
                ToDictionary(Function(s) s.LockerId)

                Dim statusCreated As Integer = 0

                For Each l In existingByKey.Values
                    If statusByLockerId.ContainsKey(l.LockerId) Then Continue For

                    Dim st As New LockerStatus With {
                .LockerId = l.LockerId,
                .LockState = LockState.Unknown,
                .OccupancyState = OccupancyState.OutOfService,   ' Not ready until commissioned
                .PackagePresent = Nothing,
                .LastUpdatedUtc = DateTime.UtcNow,
                .LastActorId = AdminActorID,
                .LastReason = "Auto-created during commissioning scan."
            }

                    db.LockerStatuses.Add(st)
                    statusCreated += 1
                Next

                db.SaveChanges()

                StatusText.Text = $"Upsert complete. Created lockers={createdCount}, already existed={existingCount}, status created={statusCreated}."
            End Using
        End Sub
        Private Function ProbeRelaysCore(ports As List(Of ControllerPort), maxRelaysOverride As Integer) As List(Of RelayProbeRow)
            Dim rows As New List(Of RelayProbeRow)

            For Each p In ports
                Dim branch = (If(p.BranchName, "")).Trim().ToUpperInvariant()
                Dim portName = (If(p.PortName, "")).Trim()

                Dim board As HldLockBoard = Nothing
                Try
                    board = New HldLockBoard()
                    board.Open(portName, 115200)
                    System.Threading.Thread.Sleep(200)

                    ' Detect boards
                    Dim layerCount As Integer = 0
                    Try : layerCount = board.GetLayerCount() : Catch : End Try

                    Dim detectedBoards As Integer = Math.Max(layerCount, Math.Max(board.boardCount, board.LayerCount))
                    Dim detectedRelays As Integer = If(detectedBoards > 0, detectedBoards * 8, maxRelaysOverride)

                    Dim maxRelays = Math.Min(maxRelaysOverride, detectedRelays)

                    For relayId As Integer = 1 To maxRelays
                        Dim r As New RelayProbeRow With {
                    .Branch = branch,
                    .PortName = portName,
                    .RelayId = relayId,
                    .Success = False
                }

                        Try
                            r.LockStatus = board.GetLockStatus(relayId)
                            r.SensorStatus = board.GetSensorStatus(relayId)
                            r.Success = True
                        Catch ex As Exception
                            r.Success = False
                            r.ErrorMessage = ex.GetType().Name & ": " & ex.Message
                        End Try

                        rows.Add(r)
                    Next

                Catch ex As Exception
                    ' If we can't open the port at all, emit one row to show it clearly
                    rows.Add(New RelayProbeRow With {
                .Branch = branch,
                .PortName = portName,
                .RelayId = 0,
                .Success = False,
                .ErrorMessage = "Open failed: " & ex.GetType().Name & ": " & ex.Message
            })

                Finally
                    If board IsNot Nothing Then
                        Try : board.Close() : Catch : End Try
                    End If
                End Try
            Next

            Return rows
        End Function
        Private Function BuildProbeReport(rows As IEnumerable(Of RelayScanRow)) As String
            Dim list As List(Of RelayScanRow) =
        If(rows, Enumerable.Empty(Of RelayScanRow)()).
        Where(Function(r) r IsNot Nothing AndAlso r.RelayId > 0).
        OrderBy(Function(r) r.Branch).
        ThenBy(Function(r) r.PortName).
        ThenBy(Function(r) r.RelayId).
        ToList()

            Dim sb As New StringBuilder()

            Dim ok = list.Where(Function(r) r.Success).ToList()
            Dim fail = list.Where(Function(r) Not r.Success).ToList()

            sb.AppendLine($"Probe summary: OK={ok.Count}, FAIL={fail.Count}")
            sb.AppendLine()

            ' Count changes AFTER unlock
            Dim changedLock As Integer = System.Linq.Enumerable.Count(ok,
    Function(r) r.LockStatus.HasValue AndAlso
                r.LockStatusAfter.HasValue AndAlso
                r.LockStatusAfter.Value <> r.LockStatus.Value)

            Dim changedSensor As Integer = System.Linq.Enumerable.Count(ok,
    Function(r) r.SensorStatus.HasValue AndAlso
                r.SensorStatusAfter.HasValue AndAlso
                r.SensorStatusAfter.Value <> r.SensorStatus.Value)



            sb.AppendLine($"Unlock-probe changes: LockChanged={changedLock}, SensorChanged={changedSensor}")
                 sb.AppendLine()

                 ' BEFORE patterns
                 sb.AppendLine("OK patterns (BEFORE) Lock,Sensor -> Count:")
                 For Each grp In ok.
             GroupBy(Function(r) $"{NullableToText(r.LockStatus)},{NullableToText(r.SensorStatus)}").
             OrderByDescending(Function(gp) gp.Count())

                     sb.AppendLine($"  {grp.Key} -> {grp.Count()}")
                 Next
                 sb.AppendLine()

                 ' BEFORE->AFTER patterns
                 Dim okWithAfter = ok.Where(Function(r) r.LockStatusAfter.HasValue OrElse r.SensorStatusAfter.HasValue).ToList()
                 If okWithAfter.Count > 0 Then
                     sb.AppendLine("OK patterns (BEFORE -> AFTER) Lock,Sensor -> Count:")
                     For Each grp In okWithAfter.
                 GroupBy(Function(r)
                             Dim beforeKey = $"{NullableToText(r.LockStatus)},{NullableToText(r.SensorStatus)}"
                             Dim afterKey = $"{NullableToText(r.LockStatusAfter)},{NullableToText(r.SensorStatusAfter)}"
                             Return $"{beforeKey} -> {afterKey}"
                         End Function).
                 OrderByDescending(Function(gp) gp.Count())

                         sb.AppendLine($"  {grp.Key} -> {grp.Count()}")
                     Next
                     sb.AppendLine()
                 End If

                 ' Failures
                 If fail.Count > 0 Then
                     sb.AppendLine("Failures (first 15):")
                     For Each r In fail.Take(15)
                         sb.AppendLine($"  {r.Branch} {r.PortName} Relay {r.RelayId:000}: {If(r.ErrorMessage, "")}")
                     Next
                     sb.AppendLine()
                 End If

                 ' Detailed per relay
                 sb.AppendLine("Relay details:")
                 For Each r In list
                     If Not r.Success Then
                         sb.AppendLine($"  {r.Branch} {r.PortName} Relay {r.RelayId:000}: FAIL {If(r.ErrorMessage, "")}")
                         Continue For
                     End If

                     Dim beforeTxt = $"Lock={NullableToText(r.LockStatus)} Sensor={NullableToText(r.SensorStatus)}"
                     Dim afterTxt = $"Lock={NullableToText(r.LockStatusAfter)} Sensor={NullableToText(r.SensorStatusAfter)}"

                     Dim flags As String = ""
                     If r.LockStatus.HasValue AndAlso r.LockStatusAfter.HasValue AndAlso r.LockStatusAfter.Value <> r.LockStatus.Value Then flags &= " LockChanged"
                     If r.SensorStatus.HasValue AndAlso r.SensorStatusAfter.HasValue AndAlso r.SensorStatusAfter.Value <> r.SensorStatus.Value Then flags &= " SensorChanged"
                     If Not String.IsNullOrWhiteSpace(flags) Then flags = " [" & flags.Trim() & "]"

                     sb.AppendLine($"  {r.Branch} {r.PortName} Relay {r.RelayId:000}: {beforeTxt} -> {afterTxt}{flags}")

                     ' Include timeline ONLY when lock changed (keeps report readable)
                     Dim lockChanged As Boolean =
                 (r.LockStatus.HasValue AndAlso r.LockStatusAfter.HasValue AndAlso r.LockStatusAfter.Value <> r.LockStatus.Value)

                     If lockChanged AndAlso Not String.IsNullOrWhiteSpace(r.Samples) Then
                         sb.AppendLine("    Timeline (120ms samples):")
                         For Each line In r.Samples.Split({Environment.NewLine}, StringSplitOptions.None)
                             If line.Length > 0 Then sb.AppendLine("      " & line)
                         Next
                     End If
                 Next

                 Return sb.ToString()
             End Function

        Private Shared Function NullableToText(v As Integer?) As String
            If v.HasValue Then Return v.Value.ToString()
            Return "-"
        End Function

        Private Function ProbeRelay(board As HldLockBoard,
                            relayId As Integer) _
    As (ls1 As Integer, ss1 As Integer, ls2 As Integer, ss2 As Integer)

            ' Baseline
            Dim ls1 As Integer = board.GetLockStatus(relayId)
            Dim ss1 As Integer = board.GetSensorStatus(relayId)

            ' Unlock
            board.Unlock(relayId)

            ' Mandatory settle time after unlock
            System.Threading.Thread.Sleep(200)

            Dim ls2 As Integer = ls1
            Dim ss2 As Integer = ss1

            Dim sw As Stopwatch = Stopwatch.StartNew()

            Do
                ' Respect >=100ms query interval
                System.Threading.Thread.Sleep(120)

                ls2 = board.GetLockStatus(relayId)
                ss2 = board.GetSensorStatus(relayId)

                If ls2 <> ls1 Then Exit Do

            Loop While sw.ElapsedMilliseconds < 5000   ' SDK timeout

            ' Enforce unlock spacing for NEXT relay
            System.Threading.Thread.Sleep(600)

            Return (ls1, ss1, ls2, ss2)
        End Function
        Private Function WaitForLockChange(board As HldLockBoard,
                                   relayId As Integer,
                                   beforeVal As Integer,
                                   timeoutMs As Integer) As Integer

            Dim sw As Stopwatch = Stopwatch.StartNew()
            Dim lastVal As Integer = beforeVal

            ' initial settle after unlock
            System.Threading.Thread.Sleep(200)

            Do While sw.ElapsedMilliseconds < timeoutMs
                ' query interval >=100ms
                System.Threading.Thread.Sleep(120)

                Dim v As Integer = board.GetLockStatus(relayId)
                lastVal = v

                If v <> beforeVal Then
                    Return v
                End If
            Loop

            Return lastVal ' no change observed
        End Function

        ' ---------- Helpers ----------
        Private Shared Function NormalizeBranch(branch As String) As String
            Dim b = (If(branch, "")).Trim().ToUpperInvariant()
            If b <> "A" AndAlso b <> "B" Then Return b ' validation will catch
            Return b
        End Function
        Private Shared Function NormalizeSizeCode(sizeCode As String) As String
            Return (If(sizeCode, "")).Trim().ToUpperInvariant()
        End Function
        Private Shared Function NormalizeLockerNumber(lockerNumber As String) As String
            Return (If(lockerNumber, "")).Trim()
        End Function
        Private Function NextSuggestedLockerNumber() As String
            If _rows Is Nothing OrElse _rows.Count = 0 Then
                Return "001"
            End If

            Dim maxNum As Integer = 0

            For Each r In _rows
                If r Is Nothing Then Continue For

                Dim s = (If(r.LockerNumber, "")).Trim()
                If s.Length = 0 Then Continue For

                ' Pull trailing digits if present (e.g., "A-012" -> 12, "012" -> 12)
                Dim i As Integer = s.Length - 1
                While i >= 0 AndAlso Char.IsDigit(s(i))
                    i -= 1
                End While

                Dim digits = s.Substring(i + 1)
                If digits.Length = 0 Then Continue For

                Dim n As Integer
                If Integer.TryParse(digits, n) Then
                    If n > maxNum Then maxNum = n
                End If
            Next

            Dim nextNum = maxNum + 1
            Return nextNum.ToString("000")  ' 3-digit suggestion
        End Function
        Private Function ValidateRows() As String

            Dim usedAddr As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim usedLockerNumber As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            ' Pull enabled size codes from DB (if available). If not available, we won't hard-fail size codes here.
            Dim enabledSizeCodes As HashSet(Of String) = Nothing
            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim codes =
                        db.LockerSizes.
                           AsNoTracking().
                           Where(Function(s) s.IsEnabled).
                           Select(Function(s) s.SizeCode).
                           ToList()

                    enabledSizeCodes = New HashSet(Of String)(
                        codes.Select(Function(c) (If(c, "")).Trim().ToUpperInvariant()).
                              Where(Function(c) c.Length > 0),
                        StringComparer.OrdinalIgnoreCase
                    )
                End Using
            Catch
                enabledSizeCodes = Nothing
            End Try

            For Each r In _rows
                If r Is Nothing Then Continue For

                Dim branch = NormalizeBranch(r.Branch)
                If branch <> "A" AndAlso branch <> "B" Then
                    Return $"LockerNumber '{r.LockerNumber}': Branch must be A or B."
                End If

                If r.RelayId < 1 OrElse r.RelayId > 200 Then
                    Return $"LockerNumber '{r.LockerNumber}': RelayId must be 1–200."
                End If

                Dim addrKey = $"{branch}:{r.RelayId}"
                If Not usedAddr.Add(addrKey) Then
                    Return $"Duplicate Branch + RelayId: {branch}-{r.RelayId}"
                End If

                Dim ln = NormalizeLockerNumber(r.LockerNumber)
                If ln.Length = 0 Then
                    Return "LockerNumber is required."
                End If
                If Not usedLockerNumber.Add(ln) Then
                    Return $"Duplicate LockerNumber: {ln}"
                End If

                Dim sc = NormalizeSizeCode(r.SizeCode)
                If sc.Length = 0 Then
                    Return $"SizeCode is required for LockerNumber '{ln}'."
                End If

                ' If we successfully loaded enabled codes from DB, enforce membership.
                If enabledSizeCodes IsNot Nothing AndAlso enabledSizeCodes.Count > 0 Then
                    If Not enabledSizeCodes.Contains(sc) Then
                        Return $"Invalid or disabled SizeCode '{sc}' for LockerNumber '{ln}'."
                    End If
                End If
            Next

            Return Nothing

        End Function
        Private Sub DiagnosticProbePort(branchName As String, portName As String)
            Dim sb As New StringBuilder()
            sb.AppendLine($"Diagnostic probe: Branch={branchName} Port={portName}")

            Dim board As HldLockBoard = Nothing
            Try
                board = New HldLockBoard()
                board.Open(portName, 115200)
                System.Threading.Thread.Sleep(200)

                ' These are useful "is it really a board?" signals.
                Dim layerCount As Integer = -1
                Dim boardCount As Integer = -1
                Dim layerCountProp As Integer = -1

                Try : layerCount = board.GetLayerCount() : Catch ex As Exception : sb.AppendLine("GetLayerCount EX: " & ex.Message) : End Try
                Try : boardCount = board.boardCount : Catch ex As Exception : sb.AppendLine("boardCount EX: " & ex.Message) : End Try
                Try : layerCountProp = board.LayerCount : Catch ex As Exception : sb.AppendLine("LayerCount prop EX: " & ex.Message) : End Try

                sb.AppendLine($"Counts: GetLayerCount={layerCount}, boardCount={boardCount}, LayerCountProp={layerCountProp}")
                sb.AppendLine()

                ' Now actively test a few relay IDs.
                For relayId As Integer = 1 To 16
                    Dim ls1 As Integer = board.GetLockStatus(relayId)
                    Dim ss1 As Integer = board.GetSensorStatus(relayId)

                    ' Try an unlock pulse and re-read quickly.
                    Try
                        board.Unlock(relayId)
                    Catch ex As Exception
                        sb.AppendLine($"Relay {relayId:00}: Unlock EX: {ex.Message}")
                        Continue For
                    End Try

                    System.Threading.Thread.Sleep(150)

                    Dim ls2 As Integer = board.GetLockStatus(relayId)
                    Dim ss2 As Integer = board.GetSensorStatus(relayId)

                    sb.AppendLine($"Relay {relayId:00}: Lock {ls1}->{ls2}, Sensor {ss1}->{ss2}")
                Next

                MessageBox.Show(sb.ToString(), "Controller Diagnostic", MessageBoxButton.OK, MessageBoxImage.Information)

            Catch ex As Exception
                MessageBox.Show("Open/Probe failed: " & ex.Message, "Controller Diagnostic", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                If board IsNot Nothing Then
                    Try : board.Close() : Catch : End Try
                End If
            End Try
        End Sub
        Private Sub RunSingleDiagnostic()
            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim p = db.ControllerPorts.
            AsNoTracking().
            Where(Function(x) x.IsEnabled).
            FirstOrDefault()

                If p Is Nothing Then
                    MessageBox.Show("No enabled ControllerPort found.", "Diagnostic", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                DiagnosticProbePort(p.BranchName, p.PortName)
            End Using
        End Sub
        Private Function ProbeRelayWithTimeline(board As HldLockBoard,
                                        relayId As Integer,
                                        samplesToTake As Integer,
                                        sampleIntervalMs As Integer,
                                        settleAfterUnlockMs As Integer,
                                        unlockSpacingMs As Integer,
                                        timeoutMs As Integer) _
    As (ls1 As Integer, ss1 As Integer, ls2 As Integer, ss2 As Integer, timeline As String, sampleCount As Integer)

            If board Is Nothing Then Throw New ArgumentNullException(NameOf(board))
            If relayId < 1 OrElse relayId > 255 Then Throw New ArgumentOutOfRangeException(NameOf(relayId))
            If samplesToTake < 1 Then samplesToTake = 1
            If sampleIntervalMs < 100 Then sampleIntervalMs = 100
            If settleAfterUnlockMs < 0 Then settleAfterUnlockMs = 0
            If unlockSpacingMs < 600 Then unlockSpacingMs = 600
            If timeoutMs < 1000 Then timeoutMs = 1000

            Dim sb As New StringBuilder()

            ' Baseline reads
            Dim ls1 As Integer = board.GetLockStatus(relayId)
            Dim ss1 As Integer = board.GetSensorStatus(relayId)
            sb.AppendLine($"Baseline: Lock={ls1}, Sensor={ss1}")

            ' Unlock
            board.Unlock(relayId)
            sb.AppendLine("Unlock issued")

            ' Settle
            If settleAfterUnlockMs > 0 Then
                System.Threading.Thread.Sleep(settleAfterUnlockMs)
            End If

            ' Sample timeline @ interval
            Dim lastLs As Integer = ls1
            Dim lastSs As Integer = ss1

            Dim sw As Stopwatch = Stopwatch.StartNew()

            For i As Integer = 1 To samplesToTake
                System.Threading.Thread.Sleep(sampleIntervalMs)

                Dim ls As Integer = board.GetLockStatus(relayId)
                Dim ss As Integer = board.GetSensorStatus(relayId)

                lastLs = ls
                lastSs = ss

                sb.AppendLine($"T+{i * sampleIntervalMs}ms: Lock={ls}, Sensor={ss}")

                ' Respect overall timeout (single command query timeout is 5s, but this keeps the probe bounded)
                If sw.ElapsedMilliseconds >= timeoutMs Then Exit For
            Next

            ' Enforce unlock spacing for NEXT relay
            System.Threading.Thread.Sleep(unlockSpacingMs)

            Return (ls1, ss1, lastLs, lastSs, sb.ToString(), Math.Min(samplesToTake, CInt(sw.ElapsedMilliseconds \ sampleIntervalMs)))
        End Function
        Private Sub Diagnostic_StatusMatrix_NoUnlock(branchName As String, portName As String)
            Const sampleEveryMs As Integer = 120
            Const totalMs As Integer = 6000
            Const maxRelays As Integer = 8   ' change to 16 when you want

            Dim sb As New StringBuilder()
            sb.AppendLine($"Status matrix (NO UNLOCK). Branch={branchName} Port={portName}")
            sb.AppendLine("Manually trip ONE lock switch while this is running.")
            sb.AppendLine($"Sampling GetLockStatus(1..{maxRelays}) every {sampleEveryMs}ms for {totalMs \ 1000} seconds.")
            sb.AppendLine()

            ' Header
            sb.Append("T+ms".PadLeft(6)).Append("  ")
            For i As Integer = 1 To maxRelays
                sb.Append($"L{i:00} ").Append(" ")
            Next
            sb.AppendLine()

            Dim board As HldLockBoard = Nothing
            Try
                board = New HldLockBoard()
                board.Open(portName, 115200)
                System.Threading.Thread.Sleep(200)

                Dim sw As Stopwatch = Stopwatch.StartNew()

                Do
                    Dim t As Integer = CInt(sw.ElapsedMilliseconds)

                    ' Read all relays first (so printing is never out-of-sync)
                    Dim vals(maxRelays) As Integer  ' 1..maxRelays, index 0 unused
                    For i As Integer = 1 To maxRelays
                        vals(i) = board.GetLockStatus(i)
                    Next

                    ' Print row
                    sb.Append(t.ToString().PadLeft(6)).Append("  ")
                    For i As Integer = 1 To maxRelays
                        sb.Append(vals(i).ToString().PadLeft(2)).Append(" ")
                        sb.Append(" ")
                    Next
                    sb.AppendLine()

                    System.Threading.Thread.Sleep(sampleEveryMs)

                Loop While sw.ElapsedMilliseconds < totalMs

                MessageBox.Show(sb.ToString(), "Status Matrix (No Unlock)", MessageBoxButton.OK, MessageBoxImage.Information)

            Catch ex As Exception
                MessageBox.Show("Diagnostic failed: " & ex.Message, "Status Matrix (No Unlock)", MessageBoxButton.OK, MessageBoxImage.Error)

            Finally
                If board IsNot Nothing Then
                    Try : board.Close() : Catch : End Try
                End If
            End Try
        End Sub
        Private Sub Diagnostic_UnlockOne_ReadAll(branchName As String, portName As String, unlockRelayId As Integer)
            Dim sb As New StringBuilder()
            sb.AppendLine($"Unlock-one / Read-all. Branch={branchName} Port={portName}")
            sb.AppendLine($"Unlock relay: {unlockRelayId}")
            sb.AppendLine("Reads GetLockStatus(1..16) baseline, then every 120ms for 3 seconds.")
            sb.AppendLine()

            Dim board As HldLockBoard = Nothing
            Try
                board = New HldLockBoard()
                board.Open(portName, 115200)
                System.Threading.Thread.Sleep(200)

                Const maxRelay As Integer = 16
                Const intervalMs As Integer = 120
                Const totalMs As Integer = 3000

                Dim baseline(maxRelay) As Integer
                For i As Integer = 1 To maxRelay
                    baseline(i) = board.GetLockStatus(i)
                Next

                sb.AppendLine("Baseline:")
                sb.AppendLine(String.Join(" ", Enumerable.Range(1, maxRelay).Select(Function(i) $"L{i:00}={baseline(i)}")))
                sb.AppendLine()

                ' Unlock ONE relay
                board.Unlock(unlockRelayId)

                ' Respect >=100ms query interval; also allow settle
                System.Threading.Thread.Sleep(200)

                Dim elapsed As Integer = 0
                While elapsed <= totalMs
                    Dim current(maxRelay) As Integer
                    For i As Integer = 1 To maxRelay
                        current(i) = board.GetLockStatus(i)
                    Next

                    ' Show which relays changed from baseline at this timestamp
                    Dim changed = New List(Of String)()
                    For i As Integer = 1 To maxRelay
                        If current(i) <> baseline(i) Then
                            changed.Add($"L{i:00}:{baseline(i)}->{current(i)}")
                        End If
                    Next

                    sb.AppendLine($"T+{elapsed}ms changed: {(If(changed.Count = 0, "(none)", String.Join(", ", changed)))}")

                    System.Threading.Thread.Sleep(intervalMs)
                    elapsed += intervalMs
                End While

                SaveAndShowReport("UnlockOne_ReadAll", sb.ToString())

            Catch ex As Exception
                MessageBox.Show("Open/Probe failed: " & ex.Message, "Unlock One / Read All", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                If board IsNot Nothing Then
                    Try : board.Close() : Catch : End Try
                End If
            End Try
        End Sub
        Private Sub RunMappingDiagnostics()
            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim p = db.ControllerPorts.AsNoTracking().
            Where(Function(x) x.IsEnabled).
            FirstOrDefault()

                If p Is Nothing Then
                    MessageBox.Show("No enabled ControllerPort found.", "Diagnostic", MessageBoxButton.OK, MessageBoxImage.Warning)
                    Return
                End If

                ' 1) No unlock: you manually trip a lock switch while it samples
                Diagnostic_StatusMatrix_NoUnlock(p.BranchName, p.PortName)

                ' 2) Unlock relay 3 and see WHICH lock status changes
                Diagnostic_UnlockOne_ReadAll(p.BranchName, p.PortName, 3)
            End Using

            MessageBox.Show(
      "Controller mapping diagnostics COMPLETE." & Environment.NewLine &
      "Review the output and report files.",
      "Diagnostics Finished",
      MessageBoxButton.OK,
      MessageBoxImage.Information
  )
        End Sub
        Private Sub SaveAndShowReport(title As String, content As String)
            Dim path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}.txt")

            System.IO.File.WriteAllText(path, content)

            MessageBox.Show(
        $"{content}{Environment.NewLine}{Environment.NewLine}Saved to:{Environment.NewLine}{path}",
        title,
        MessageBoxButton.OK,
        MessageBoxImage.Information)
        End Sub
        Private Sub SeedLiveRows()
            _liveRows.Clear()
            For i As Integer = 1 To 8
                _liveRows.Add(New LiveMonitorRow With {
            .RelayId = i,
            .LockStatus = Nothing,
            .SensorStatus = Nothing,
            .LastChangeMs = 0,
            .Notes = ""
        })
            Next
        End Sub
        Private Async Sub StartLiveMonitor_Click(sender As Object, e As RoutedEventArgs)
            If _liveTask IsNot Nothing AndAlso Not _liveTask.IsCompleted Then Return

            StartLiveMonitorButton.IsEnabled = False
            StopLiveMonitorButton.IsEnabled = True

            LiveMonitorStatusText.Text = "Starting..."
            _liveStartedUtc = DateTime.UtcNow

            ' pick a port to monitor (enabled ControllerPort)
            Dim branch As String = Nothing
            Dim portName As String = Nothing

            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim p = db.ControllerPorts.AsNoTracking().
            Where(Function(x) x.IsEnabled).
            OrderBy(Function(x) x.BranchName).
            ThenBy(Function(x) x.PortName).
            FirstOrDefault()

                If p Is Nothing Then
                    LiveMonitorStatusText.Text = "No enabled ControllerPort found."
                    StartLiveMonitorButton.IsEnabled = True
                    StopLiveMonitorButton.IsEnabled = False
                    Return
                End If

                branch = (If(p.BranchName, "")).Trim().ToUpperInvariant()
                portName = (If(p.PortName, "")).Trim()
            End Using

            ' Reset display
            SeedLiveRows()
            LiveMonitorGrid.Items.Refresh()

            _liveCts = New System.Threading.CancellationTokenSource()

            Try
                ' Open the board ONCE and keep it open while monitoring
                _liveBoard = New HldLockBoard()
                _liveBoard.Open(portName, 115200)
                System.Threading.Thread.Sleep(200)

                LiveMonitorStatusText.Text = $"Monitoring Branch={branch} Port={portName} (1–8)..."

                _liveTask = Task.Run(Sub() LiveMonitorLoop(branch, portName, _liveCts.Token))

            Catch ex As Exception
                LiveMonitorStatusText.Text = $"Start failed: {ex.Message}"
                CleanupLiveMonitor()
                StartLiveMonitorButton.IsEnabled = True
                StopLiveMonitorButton.IsEnabled = False
            End Try
        End Sub
        Private Sub StopLiveMonitor_Click(sender As Object, e As RoutedEventArgs)
            StopLiveMonitorButton.IsEnabled = False
            LiveMonitorStatusText.Text = "Stopping..."

            Try
                If _liveCts IsNot Nothing Then
                    _liveCts.Cancel()
                End If
            Catch
            End Try
        End Sub
        Private Sub LiveMonitorLoop(branch As String, portName As String, token As System.Threading.CancellationToken)
            Const intervalMs As Integer = 150 ' >=100ms per SDK guidance

            Try
                While Not token.IsCancellationRequested
                    Dim nowMs As Integer = CInt((DateTime.UtcNow - _liveStartedUtc).TotalMilliseconds)

                    ' read all relays first (no UI work here)
                    Dim lockVals(8) As Integer
                    Dim sensorVals(8) As Integer

                    For i As Integer = 1 To 8
                        lockVals(i) = _liveBoard.GetLockStatus(i)
                        sensorVals(i) = _liveBoard.GetSensorStatus(i)
                    Next

                    ' apply to bound rows on UI thread
                    Dispatcher.Invoke(Sub()
                                          For i As Integer = 1 To 8
                                              Dim row = _liveRows(i - 1)

                                              Dim newLock As Integer? = lockVals(i)
                                              Dim newSensor As Integer? = sensorVals(i)

                                              Dim changed As Boolean =
                                          (row.LockStatus.HasValue AndAlso row.LockStatus.Value <> newLock.Value) OrElse
                                          (row.SensorStatus.HasValue AndAlso row.SensorStatus.Value <> newSensor.Value)

                                              ' first fill (no change note)
                                              If Not row.LockStatus.HasValue AndAlso Not row.SensorStatus.HasValue Then
                                                  changed = False
                                              End If

                                              row.LockStatus = newLock
                                              row.SensorStatus = newSensor

                                              If changed Then
                                                  row.LastChangeMs = nowMs
                                                  row.Notes = "Changed"
                                              End If
                                          Next

                                          ' If you add INotifyPropertyChanged later, you can remove this refresh.
                                          LiveMonitorGrid.Items.Refresh()
                                          LiveMonitorStatusText.Text = $"Monitoring... T+{nowMs}ms"
                                      End Sub)

                    System.Threading.Thread.Sleep(intervalMs)
                End While

            Catch ex As Exception
                Dispatcher.Invoke(Sub()
                                      LiveMonitorStatusText.Text = $"Monitor error: {ex.Message}"
                                  End Sub)
            Finally
                Dispatcher.Invoke(Sub()
                                      CleanupLiveMonitor()
                                      LiveMonitorStatusText.Text = "Monitor stopped."
                                      StartLiveMonitorButton.IsEnabled = True
                                      StopLiveMonitorButton.IsEnabled = False
                                  End Sub)
            End Try
        End Sub
        Private Sub CleanupLiveMonitor()
            Try
                If _liveCts IsNot Nothing Then
                    _liveCts.Dispose()
                    _liveCts = Nothing
                End If
            Catch
            End Try

            If _liveBoard IsNot Nothing Then
                Try : _liveBoard.Close() : Catch : End Try
                _liveBoard = Nothing
            End If
        End Sub

    End Class
End Namespace