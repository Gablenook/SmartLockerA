Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Data
Imports System.Diagnostics
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Microsoft.Data.Sqlite
Imports Microsoft.EntityFrameworkCore


Namespace SmartLockerKiosk
    Partial Public Class LockerCommissioningWindow
        Inherits Window

        Private ReadOnly _lockerController As LockerControllerService

        Public Property ActorId As String
        Public Property KioskId As String

        ' ===== Relay layout =====
        Private Const Boards As Integer = 25
        Private Const PortsPerBoard As Integer = 8
        Private Const TotalRelays As Integer = Boards * PortsPerBoard ' 200

        ' Debounce state per branch (allocated in ctor)
        Private _haveLastShownA() As Boolean
        Private _lastShownOpenA() As Boolean
        Private _closedStreakA() As Integer

        Private _haveLastShownB() As Boolean
        Private _lastShownOpenB() As Boolean
        Private _closedStreakB() As Integer

        ' Guard so StatusUpdated can't re-enter
        Private _updatingStatus As Integer = 0

        ' Commissioning state
        Private _openAllIssued As Boolean = False
        Private _confirmed As Boolean = False
        Private _saved As Boolean = False

        ' Lock open control variables
        Private _isOpeningAll As Boolean = False
        Private Const OpenAllPasses As Integer = 2

        ' ===== UI model =====
        Private ReadOnly _vm As New LockerCommissioningVm()
        Private _eventsWired As Boolean = False
        Private _branchesConnected As Boolean = False
        Private _suspendStatusRefresh As Boolean = False

        Private _openAllCts As System.Threading.CancellationTokenSource = Nothing

        Private Const ScanStep As Integer = 8   ' one board at a time feels right
        Public Enum CommissioningExitMode
            BackToControllerAssignment
            ContinueToNextStep
            AbortCommissioning
        End Enum

        Private _isAssigningDoors As Boolean = False
        Private _nextLockerTag As Integer = 1

        Public Event ExitRequested(mode As CommissioningExitMode)

        Private Class DoorAssignment
            Public Property LockerTag As Integer          ' physical label: 1,2,3...
            Public Property Branch As String              ' "A" or "B"
            Public Property RelayId As Integer            ' 1..200
            Public Property SizeCode As String            ' computed later
        End Class
        Private ReadOnly _assignments As New List(Of DoorAssignment)()
        Private ReadOnly _assignedRelayKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Private Function RelayKey(branch As String, relayId As Integer) As String
            Return $"{branch}:{relayId}"
        End Function

        Public Sub New(lockerController As LockerControllerService)
            InitializeComponent()
            Me.DataContext = _vm

            If lockerController Is Nothing Then Throw New ArgumentNullException(NameOf(lockerController))
            _lockerController = lockerController

            ' Allocate debounce arrays AFTER constants exist and AFTER ctor begins
            ReDim _haveLastShownA(TotalRelays - 1)
            ReDim _lastShownOpenA(TotalRelays - 1)
            ReDim _closedStreakA(TotalRelays - 1)

            ReDim _haveLastShownB(TotalRelays - 1)
            ReDim _lastShownOpenB(TotalRelays - 1)
            ReDim _closedStreakB(TotalRelays - 1)
        End Sub
        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            If String.IsNullOrWhiteSpace(ActorId) Then ActorId = "System:Commissioning"
            If String.IsNullOrWhiteSpace(KioskId) Then KioskId = "KIOSK-UNKNOWN"

            _vm.InitCells()

            WireServiceEventsOnce()
            RefreshBranchTitles()

            ' Initial UI state
            StartControllersButton.IsEnabled = True
            OpenAllButton.IsEnabled = False
            ConfirmedButton.IsEnabled = False
            ExitButton.IsEnabled = True

            HighlightStep(1)

            SetCallToAction(StartControllersButton, True)
            SetCallToAction(OpenAllButton, False)
            StatusText.Text = "Step 1: Connect Controllers."
        End Sub
        Private Sub Window_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
            ' Best-effort shutdown
            Try
                _lockerController.Dispose()
            Catch ex As Exception
                Debug.WriteLine("LockerController shutdown error: " & ex.Message)
            End Try
        End Sub
        Private Sub WireServiceEventsOnce()
            If _eventsWired Then Return
            _eventsWired = True

            AddHandler _lockerController.BranchStatusUpdated, AddressOf OnBranchStatusUpdated
            AddHandler _lockerController.Disconnected, AddressOf OnBranchDisconnected
            AddHandler _lockerController.Reconnected, AddressOf OnBranchReconnected
        End Sub
        Private Sub RefreshBranchTitles()
            Dim bEnabled As Boolean = _lockerController.IsBranchEnabled("B")

            _vm.BranchATitle = "Branch A"
            _vm.BranchBTitle = If(bEnabled, "Branch B", "Branch B (disabled)")

        End Sub
        ' ---------- Connect ----------
        Private Async Sub StartControllers_Click(sender As Object, e As RoutedEventArgs) Handles StartControllersButton.Click
            Try
                StatusText.Text = "Connecting…"
                StartControllersButton.IsEnabled = False
                OpenAllButton.IsEnabled = False

                ' IMPORTANT: connect only once
                If Not _branchesConnected Then
                    _lockerController.ConnectBranch("A")

                    If _lockerController.IsBranchEnabled("B") Then
                        _lockerController.ConnectBranch("B")
                    End If

                    _branchesConnected = True
                End If

                ' Give controllers a moment to deliver first frame(s) so we can report readiness
                Await Task.Delay(400)

                Dim aReady As Boolean = False
                Dim bEnabled As Boolean = _lockerController.IsBranchEnabled("B")
                Dim bReady As Boolean = False

                Try
                    aReady = _lockerController.IsBranchReady("A") AndAlso _lockerController.IsCommsHealthy("A")
                Catch
                    aReady = False
                End Try

                If bEnabled Then
                    Try
                        bReady = _lockerController.IsBranchReady("B") AndAlso _lockerController.IsCommsHealthy("B")
                    Catch
                        bReady = False
                    End Try
                End If

                Dim bLine As String
                If Not bEnabled Then
                    bLine = "B: disabled"
                ElseIf bReady Then
                    bLine = "B: ready"
                Else
                    bLine = "B: enabled but not ready"
                End If

                If aReady Then
                    StatusText.Text = $"Connected. A: ready, {bLine}. Step 2: Select number of lockers to scan, then press Open All."
                    OpenAllButton.IsEnabled = True

                    ' Visual guide: move CTA from Connect -> Open All
                    SetCallToAction(StartControllersButton, False)
                    SetCallToAction(OpenAllButton, True)
                Else
                    StatusText.Text = $"Connected, but Branch A not ready yet. A: not ready, {bLine}. Check COM port / power."
                    OpenAllButton.IsEnabled = False

                    ' Keep CTA on Connect (installer should re-try / troubleshoot)
                    SetCallToAction(StartControllersButton, True)
                    SetCallToAction(OpenAllButton, False)
                End If

                HighlightStep(2)

            Catch ex As Exception
                StatusText.Text = "Connect failed: " & ex.GetType().Name & ": " & ex.Message
                StartControllersButton.IsEnabled = True
                SetCallToAction(StartControllersButton, True)
                SetCallToAction(OpenAllButton, False)
            End Try
        End Sub

        ' ---------- Events ----------
        Private Sub OnBranchStatusUpdated(branch As String)
            If _suspendStatusRefresh Then Return
            Dispatcher.BeginInvoke(Sub() UpdateBranchGrid(branch))
        End Sub
        Private Sub OnBranchDisconnected(branch As String, reason As String)
            Dispatcher.Invoke(Sub() StatusText.Text = $"Branch {branch} disconnected: {reason}")
        End Sub
        Private Sub OnBranchReconnected(branch As String, portName As String)
            Dispatcher.Invoke(Sub() StatusText.Text = $"Branch {branch} reconnected: {portName}")
        End Sub

        ' ---------- Grid updates ----------
        Private Sub UpdateBranchGrid(branch As String)
            If Not Me.IsLoaded Then Return

            ' Prevent overlap if another status frame arrives while we're painting
            If Interlocked.Exchange(_updatingStatus, 1) = 1 Then Return

            Try
                ' Skip until we have a first frame (prevents gray flicker)
                If Not _lockerController.IsBranchReady(branch) Then Return
                If Not _lockerController.IsCommsHealthy(branch) Then Return

                Dim isA As Boolean = String.Equals(branch, "A", StringComparison.OrdinalIgnoreCase)

                ' IMPORTANT: use the same backing collection you are painting into
                Dim cells = If(isA, _vm.BranchAAll, _vm.BranchBAll)
                If cells Is Nothing OrElse cells.Count <> TotalRelays Then Return

                For relayId1Based As Integer = 1 To TotalRelays
                    Dim idx As Integer = relayId1Based - 1

                    Dim lockOpen As Boolean = False
                    Dim hasValue As Boolean = _lockerController.TryGetLockOpen(branch, relayId1Based, lockOpen)

                    Dim raw As Boolean? = If(hasValue, CType(lockOpen, Boolean?), Nothing)
                    Dim debounced As Boolean? = GetDebouncedIsOpen(raw, branch, idx)

                    Dim brush As Brush
                    If Not debounced.HasValue Then
                        brush = Brushes.LightGray
                    Else
                        brush = If(debounced.Value, Brushes.LimeGreen, Brushes.Red)
                    End If

                    Dim cell = cells(idx)
                    Dim wasOpen As Boolean = cell.IsOpen
                    Dim isOpenNow As Boolean = debounced.GetValueOrDefault(False)

                    cell.IsOpen = isOpenNow
                    cell.CellBrush = brush

                    If isOpenNow Then
                        cell.IsAttached = True
                    End If

                    ' NEW: closure detection for Assign Doors
                    If _isAssigningDoors Then
                        If cell.IsAttached AndAlso wasOpen AndAlso Not isOpenNow Then
                            TryRegisterClosure(branch, relayId1Based)
                        End If
                    End If

                    ' NEW: latch "attached" once we have ever observed OPEN
                    ' (Do NOT clear this on close; commissioning wants discovery, not momentary state.)
                    If isOpenNow Then
                        cell.IsAttached = True
                    End If
                Next

                ' During commissioning (after OpenAll), compute attached counts from latched IsAttached
                If _openAllIssued AndAlso Not _saved Then
                    Dim attachedCount As Integer = 0
                    For Each c In cells
                        If c.IsAttached Then attachedCount += 1
                    Next


                    If isA Then
                        _vm.AttachedCountA = attachedCount
                    Else
                        _vm.AttachedCountB = attachedCount
                    End If

                    ' Confirm enabled once at least 1 attached on A (adjust if you want A+B behavior)
                    ConfirmedButton.IsEnabled = (_vm.AttachedCountA > 0)
                End If

                _vm.RefreshCountLines()

            Finally
                Interlocked.Exchange(_updatingStatus, 0)
            End Try
        End Sub
        Private Function GetDebouncedIsOpen(rawIsOpen As Boolean?, branch As String, idx As Integer) As Boolean?
            If Not rawIsOpen.HasValue Then Return Nothing

            Dim isOpen As Boolean = rawIsOpen.Value

            Dim haveLast() As Boolean
            Dim lastOpen() As Boolean
            Dim streak() As Integer

            If String.Equals(branch, "A", StringComparison.OrdinalIgnoreCase) Then
                haveLast = _haveLastShownA
                lastOpen = _lastShownOpenA
                streak = _closedStreakA
            Else
                haveLast = _haveLastShownB
                lastOpen = _lastShownOpenB
                streak = _closedStreakB
            End If

            ' First observation: accept
            If Not haveLast(idx) Then
                haveLast(idx) = True
                lastOpen(idx) = isOpen
                streak(idx) = If(isOpen, 0, 1)
                Return isOpen
            End If

            ' Open: accept immediately
            If isOpen Then
                lastOpen(idx) = True
                streak(idx) = 0
                Return True
            End If

            ' Closed: require 2 consecutive closed reads
            streak(idx) += 1
            If streak(idx) >= 2 Then
                lastOpen(idx) = False
                Return False
            End If

            ' Single-frame closed glitch: keep previous display
            Return lastOpen(idx)
        End Function
        ' ---------- Commands ----------
        'Set the max relays to scan based on user input, then step through and unlock each one with a flash. Honor Stop if issued. This is the "heart" of commissioning, so it gets extra polish (status messages, flash, pacing, debounce, error handling0).
        Private Sub ScanA_Increase_Click(sender As Object, e As RoutedEventArgs)
            SetScanValue(ScanAValueText, ScanStep)
        End Sub
        Private Sub ScanA_Decrease_Click(sender As Object, e As RoutedEventArgs)
            SetScanValue(ScanAValueText, -ScanStep)
        End Sub
        Private Sub ScanB_Increase_Click(sender As Object, e As RoutedEventArgs)
            SetScanValue(ScanBValueText, ScanStep)
        End Sub
        Private Sub ScanB_Decrease_Click(sender As Object, e As RoutedEventArgs)
            SetScanValue(ScanBValueText, -ScanStep)
        End Sub
        Private Sub SetScanValue(tb As TextBlock, delta As Integer)
            Dim value As Integer
            If Not Integer.TryParse(tb.Text, value) Then value = TotalRelays

            value += delta
            If value < 1 Then value = 1
            If value > TotalRelays Then value = TotalRelays

            tb.Text = value.ToString()
        End Sub
        Private Async Sub OpenAll_Click(sender As Object, e As RoutedEventArgs) Handles OpenAllButton.Click
            OpenAllButton.IsEnabled = False
            SetCallToAction(OpenAllButton, False)  ' stop pulsing while running
            StopOpenAllButton.IsEnabled = True

            HighlightStep(2)

            If _lockerController Is Nothing Then
                StatusText.Text = "Controller service not initialized."
                Return
            End If

            If Not _branchesConnected Then
                StatusText.Text = "Controllers are not connected. Click Connect first."
                Return
            End If

            If _isOpeningAll Then Return
            _isOpeningAll = True

            OpenAllButton.IsEnabled = False
            StopOpenAllButton.IsEnabled = True

            _openAllIssued = True
            _openAllCts = New System.Threading.CancellationTokenSource()
            Dim ct = _openAllCts.Token

            Try
                Dim maxA = GetMaxRelays(ScanAValueText)
                Dim maxB = GetMaxRelays(ScanBValueText)

                ' Decide branches to scan
                Dim scanA = CanScanBranch("A")
                Dim scanB = CanScanBranch("B")

                If Not scanA AndAlso Not scanB Then
                    StatusText.Text = "No branches ready to scan (check connections / comms)."
                    Return
                End If

                If scanA Then
                    StatusText.Text = $"Open All: Branch A relays 1..{maxA} (Stop anytime)…"

                    For relayId As Integer = 1 To maxA
                        If ct.IsCancellationRequested Then Exit For

                        Await FlashCellAsync("A", relayId)
                        _lockerController.UnlockRelay("A", relayId)

                        ' Power + controller pacing
                        Await Task.Delay(800, ct)
                    Next
                Else
                    StatusText.Text = "Skipping Branch A (not ready/healthy)."
                End If

                If ct.IsCancellationRequested Then
                    StatusText.Text = "Open All stopped."
                    Return
                End If

                If scanB Then
                    StatusText.Text = $"Open All: Branch B relays 1..{maxB} (Stop anytime)…"

                    For relayId As Integer = 1 To maxB
                        If ct.IsCancellationRequested Then Exit For

                        Await FlashCellAsync("B", relayId)
                        _lockerController.UnlockRelay("B", relayId)

                        Await Task.Delay(800, ct)
                    Next
                Else
                    ' Helpful message if B is disabled or not connected
                    If _lockerController.IsBranchEnabled("B") Then
                        StatusText.Text = "Branch B enabled but not ready/healthy; skipped."
                    Else
                        StatusText.Text = "Branch B disabled; skipped."
                    End If
                End If

                If ct.IsCancellationRequested Then
                    StatusText.Text = "Open All stopped."
                Else
                    StatusText.Text = "Open All complete."
                    AssignNumbersButton.IsEnabled = True
                    SetCallToAction(AssignNumbersButton, True)
                End If

                ' After Open All complete:
                _vm.ShowAttachedOnly = True

                ' Rebuild VisibleBoards in-place (ReadOnly collection => Clear/Add)
                UpdateVisibleBoards(_vm.BranchAAll, _vm.BranchAVisibleBoards)

                If CanScanBranch("B") Then
                    UpdateVisibleBoards(_vm.BranchBAll, _vm.BranchBVisibleBoards)
                Else
                    _vm.BranchBVisibleBoards.Clear()
                End If


            Catch ex As TaskCanceledException
                StatusText.Text = "Open All stopped."
            Catch ex As Exception
                StatusText.Text = $"Open All failed: {ex.GetType().Name}: {ex.Message}"
            Finally
                Try : _openAllCts?.Dispose() : Catch : End Try
                _openAllCts = Nothing

                StopOpenAllButton.IsEnabled = False
                OpenAllButton.IsEnabled = True
                _isOpeningAll = False
            End Try
        End Sub
        Private Sub Cell_Click(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            If btn Is Nothing Then Return

            Dim cell = TryCast(btn.DataContext, RelayCellVm)

            Dim branch As String = If(cell?.Branch, "A")
            Dim relayId1Based As Integer = If(cell IsNot Nothing, cell.RelayId, CInt(btn.Tag))

            If relayId1Based < 1 OrElse relayId1Based > TotalRelays Then
                StatusText.Text = $"Invalid relayId: {relayId1Based}"
                Return
            End If

            Try
                Dim sent As Boolean = _lockerController.UnlockRelay(branch, relayId1Based)
                StatusText.Text = If(sent, $"Unlock sent: {branch}{relayId1Based}", $"Unlock not sent (branch {branch} not open).")
            Catch ex As Exception
                StatusText.Text = $"Unlock failed: {ex.GetType().Name}: {ex.Message}"
            End Try
        End Sub
        Private Sub AssignNumbers_Click(sender As Object, e As RoutedEventArgs)
            ' Guard: must have completed OpenAll first
            If Not _openAllIssued Then
                StatusText.Text = "Run Open All first so attached doors can be detected."
                Return
            End If

            If _isAssigningDoors Then
                StatusText.Text = $"Already assigning. Close locker #{_nextLockerTag}."
                Return
            End If

            ' Clear prior UI assignment state (recommended if Assign can be re-run)
            For Each c In _vm.BranchAAll
                c.IsAssigned = False
                c.AssignedLockerNumber = Nothing
            Next
            For Each c In _vm.BranchBAll
                c.IsAssigned = False
                c.AssignedLockerNumber = Nothing
            Next

            ' Enter Assign mode
            _assignments.Clear()
            _assignedRelayKeys.Clear()
            _nextLockerTag = 1
            _isAssigningDoors = True

            ' UI: stop pulsing + disable the button so it can't be pressed again
            SetCallToAction(AssignNumbersButton, False)
            AssignNumbersButton.IsEnabled = False

            ' During assign, Confirm stays disabled until we've captured all closures
            ConfirmedButton.IsEnabled = False
            SetCallToAction(ConfirmedButton, False)

            ' Exit should be allowed
            ExitButton.IsEnabled = True

            Dim expected As Integer = CountAttachedDoors()
            If expected <= 0 Then
                StatusText.Text = "No attached doors detected. Check Open All / sensors."
                _isAssigningDoors = False
                AssignNumbersButton.IsEnabled = True
                SetCallToAction(AssignNumbersButton, True)
                Return
            End If

            HighlightStep(3)

            StatusText.Text = $"Assign Numbers: Close locker #{_nextLockerTag}. Waiting for closure… (Expected {expected})"
        End Sub
        Private Function CountAttachedDoors() As Integer
            Dim total As Integer = 0

            ' A
            For Each c In _vm.BranchAAll
                If c IsNot Nothing AndAlso c.IsAttached Then total += 1
            Next

            ' B only if enabled and ready
            If CanScanBranch("B") Then
                For Each c In _vm.BranchBAll
                    If c IsNot Nothing AndAlso c.IsAttached Then total += 1
                Next
            End If

            Return total
        End Function
        Private Sub Confirmed_Click(sender As Object, e As RoutedEventArgs) Handles ConfirmedButton.Click
            HighlightStep(4)
            If _confirmed OrElse _saved Then
                StatusText.Text = "Already saved."
                Return
            End If

            Try
                If _assignments.Count = 0 Then
                    StatusText.Text = "Nothing to confirm. Run Assign Doors first."
                    Return
                End If

                ' Validate: Branch must exist
                Dim badBranch = _assignments.Where(Function(x) String.IsNullOrWhiteSpace(x.Branch) OrElse
                                                     (x.Branch.Trim().ToUpperInvariant() <> "A" AndAlso x.Branch.Trim().ToUpperInvariant() <> "B")).
                                    ToList()
                If badBranch.Any() Then
                    StatusText.Text = "Cannot save: one or more assignments missing/invalid Branch."
                    Return
                End If

                ' Validate: no duplicate locker tags
                Dim dupTags = _assignments.GroupBy(Function(x) x.LockerTag).
                                  Where(Function(g) g.Count() > 1).
                                  Select(Function(g) g.Key).
                                  ToList()
                If dupTags.Any() Then
                    StatusText.Text = "Cannot save: duplicate LockerTag(s): " & String.Join(", ", dupTags)
                    Return
                End If

                ' Validate: no duplicate (branch, relayId)
                Dim dupRelays = _assignments.GroupBy(Function(x) RelayKey(x.Branch.Trim().ToUpperInvariant(), x.RelayId)).
                                    Where(Function(g) g.Count() > 1).
                                    Select(Function(g) g.Key).
                                    ToList()
                If dupRelays.Any() Then
                    StatusText.Text = "Cannot save: duplicate relay assignment(s): " & String.Join(", ", dupRelays)
                    Return
                End If

                ComputeSizeCodes()

                _suspendStatusRefresh = True
                ConfirmedButton.IsEnabled = False
                SetCallToAction(ConfirmedButton, False)

                Using db = DatabaseBootstrapper.BuildDbContext()
                    Using tx = db.Database.BeginTransaction()

                        Dim kioskId = (If(Me.KioskId, "")).Trim()
                        If kioskId.Length = 0 Then kioskId = (If(AppSettings.KioskID, "")).Trim()

                        ' TODO (recommended): delete+replace existing mapping here if you support re-commissioning.
                        ' For now, assume fresh DB; leaving deletion out is fine for your clean-slate retest.

                        ' Insert new lockers and keep references so we only seed statuses for these

                        Dim nowUtc = DateTime.UtcNow
                        Dim newLockers As New List(Of Locker)

                        For Each a In _assignments.OrderBy(Function(x) x.LockerTag)
                            Dim row As New Locker With {
                                .LockerNumber = a.LockerTag.ToString(),
                                .RelayId = a.RelayId,
                                .Branch = a.Branch.Trim().ToUpperInvariant(),
                                .Zone = "DEFAULT",          ' keep or adjust
                                .SizeCode = a.SizeCode,
                                .IsEnabled = True,
                                .Status = New LockerStatus With {
                                    .LockState = LockState.Unknown,
                                    .OccupancyState = OccupancyState.Unknown,
                                    .LastUpdatedUtc = nowUtc
                                    }
                                    }

                            db.Lockers.Add(row)
                            newLockers.Add(row)
                        Next

                        DebugPrintForeignKeys(db)

                        db.SaveChanges()


                        ' Mark kiosk commissioned
                        Dim ks = db.KioskState.SingleOrDefault(Function(x) x.KioskId = kioskId)
                        If ks Is Nothing Then
                            ks = New KioskState With {
                        .KioskId = kioskId,
                        .LocationId = AppSettings.LocationId,
                        .IsCommissioned = True,
                        .LastUpdatedUtc = DateTime.UtcNow
                    }
                            db.KioskState.Add(ks)
                        Else
                            ks.IsCommissioned = True
                            ks.LastUpdatedUtc = DateTime.UtcNow
                        End If

                        db.SaveChanges()
                        tx.Commit()
                    End Using
                End Using

                _confirmed = True
                _saved = True
                StatusText.Text = "Confirmed + saved. Commissioning complete."
                ExitButton.IsEnabled = True
                SetCallToAction(ExitButton, True)

            Catch ex As DbUpdateException
                Dim innerType = If(ex.InnerException?.GetType().FullName, "(none)")
                Dim innerMsg = If(ex.InnerException?.Message, "(none)")

                StatusText.Text = $"DB save failed: {innerType}: {innerMsg}"
                Debug.WriteLine("=== DbUpdateException ===")
                Debug.WriteLine(ex.Message)
                Debug.WriteLine(innerType)
                Debug.WriteLine(innerMsg)
                Debug.WriteLine("InnerException: " & ex.InnerException?.ToString())

                For Each ent In ex.Entries
                    Debug.WriteLine($"Entry: {ent.Entity.GetType().Name} State={ent.State}")
                    For Each p In ent.Properties
                        Debug.WriteLine($"  {p.Metadata.Name}={p.CurrentValue}")
                    Next
                Next

                ConfirmedButton.IsEnabled = True
                SetCallToAction(ConfirmedButton, True)

            Catch ex As Exception
                StatusText.Text = $"Confirm failed: {ex.GetType().Name}: {ex.Message}"
                Debug.WriteLine(ex.Message)

                ConfirmedButton.IsEnabled = True
                SetCallToAction(ConfirmedButton, True)

            Finally
                _suspendStatusRefresh = False
            End Try
        End Sub



        '============ Helpers ============
        Private Function FindCell(branch As String, relayId As Integer) As RelayCellVm
            branch = branch.Trim().ToUpperInvariant()

            Dim cells =
        If(branch = "A",
           _vm.BranchACells,
           _vm.BranchBCells)

            Return cells.FirstOrDefault(Function(c) c.RelayId = relayId)
        End Function
        Private Async Function FlashCellAsync(branch As String, relayId As Integer) As Task
            Dim cell = FindCell(branch, relayId)
            If cell Is Nothing Then Return

            cell.FlashBrush = Brushes.Gold

            ' Allow WPF to render the highlight immediately
            Await Dispatcher.Yield(DispatcherPriority.Render)

            ' Keep flash visible
            Await Task.Delay(120)

            cell.FlashBrush = Nothing
        End Function
        Private Function GetMaxRelays(tb As TextBlock) As Integer
            Dim n As Integer
            If Not Integer.TryParse(tb.Text, n) Then n = TotalRelays
            If n < 1 Then n = 1
            If n > TotalRelays Then n = TotalRelays
            Return n
        End Function
        Private Function CanScanBranch(branch As String) As Boolean
            ' Enabled in DB?
            If Not _lockerController.IsBranchEnabled(branch) Then Return False

            ' Connected + receiving frames?
            If Not _lockerController.IsBranchReady(branch) Then Return False
            If Not _lockerController.IsCommsHealthy(branch) Then Return False

            Return True
        End Function
        Private Sub StopOpenAll_Click(sender As Object, e As RoutedEventArgs) Handles StopOpenAllButton.Click
            Try
                _openAllCts?.Cancel()
            Catch
            End Try
        End Sub
        Private Sub SetCallToAction(btn As Button, enabled As Boolean)
            If btn Is Nothing Then Return

            btn.Style = If(enabled,
                   CType(Me.FindResource("CallToActionButtonStyle"), Style),
                   CType(Me.FindResource("NormalActionButtonStyle"), Style))
        End Sub
        Private Sub TryRegisterClosure(branch As String, relayId As Integer)
            If Not _isAssigningDoors Then Return

            branch = (If(branch, "")).Trim().ToUpperInvariant()
            If branch <> "A" AndAlso branch <> "B" Then
                StatusText.Text = $"Assign failed: invalid branch '{branch}' for relay {relayId}."
                Return
            End If

            If relayId < 1 OrElse relayId > TotalRelays Then
                StatusText.Text = $"Assign failed: invalid relayId {relayId} for branch {branch}."
                Return
            End If

            Dim cell = FindCell(branch, relayId)
            If cell Is Nothing Then
                StatusText.Text = $"Assign failed: cell not found for {branch}:{relayId}."
                Return
            End If

            If Not cell.IsAttached Then Return

            Dim key = RelayKey(branch, relayId)
            If _assignedRelayKeys.Contains(key) Then Return

            ' Record assignment
            Dim a As New DoorAssignment With {
        .LockerTag = _nextLockerTag,
        .Branch = branch,
        .RelayId = relayId
    }

            _assignments.Add(a)
            _assignedRelayKeys.Add(key)

            ' ✅ NEW: mark UI as assigned (color + label)
            cell.AssignedLockerNumber = a.LockerTag.ToString()
            cell.IsAssigned = True

            ' pick a color you like for "assigned"
            cell.CellBrush = Brushes.LightGreen   ' or Brushes.Gold, etc.

            System.Media.SystemSounds.Asterisk.Play()

            _nextLockerTag += 1
            StatusText.Text = $"Recorded: Tag #{a.LockerTag} → {a.Branch}:{a.RelayId}. Close locker #{_nextLockerTag}."

            Dim expected = CountAttachedDoors()
            If _assignments.Count >= expected Then
                _isAssigningDoors = False
                StatusText.Text = $"Assign Doors complete ({_assignments.Count}). Click Confirm to save."
                ConfirmedButton.IsEnabled = True
                SetCallToAction(ConfirmedButton, True)
                ExitButton.IsEnabled = True
            End If
        End Sub
        Private Sub ComputeSizeCodes()
            ' Count used relays per board
            Dim usedPerBoard As New Dictionary(Of Integer, Integer)

            For Each a In _assignments
                Dim bp = LockerControllerService.RelayIdToBoardPort(a.RelayId)
                If Not usedPerBoard.ContainsKey(bp.Board) Then
                    usedPerBoard(bp.Board) = 0
                End If
                usedPerBoard(bp.Board) += 1
            Next

            ' Assign size codes
            For Each a In _assignments
                Dim bp = LockerControllerService.RelayIdToBoardPort(a.RelayId)
                Dim used = usedPerBoard(bp.Board)
                a.SizeCode = SizeCodeFromUsedPorts(used)
            Next
        End Sub
        Private Function SizeCodeFromUsedPorts(usedPorts As Integer) As String
            ' Your rule:
            ' 1=Very Large, 2=Large, 3=Medium, 4=Small, 5=Small, 6=Extra Small, 9=Extra Small
            Select Case usedPorts
                Case 1 : Return "E" ' pick your actual code
                Case 2 : Return "D"
                Case 3 : Return "C"
                Case 4, 5 : Return "B"
                Case 6, 9 : Return "A"
                Case Else
                    Return "B" ' sensible default
            End Select
        End Function
        Private Sub DebugPrintForeignKeys(db As KioskDbContext)
            Dim conn = TryCast(db.Database.GetDbConnection(), SqliteConnection)
            If conn Is Nothing Then Return

            If conn.State <> ConnectionState.Open Then conn.Open()

            Using cmd = conn.CreateCommand()
                cmd.CommandText = "PRAGMA foreign_key_list('Lockers');"
                Using r = cmd.ExecuteReader()
                    Debug.WriteLine("=== FK: Lockers ===")
                    While r.Read()
                        ' columns: id, seq, table, from, to, on_update, on_delete, match
                        Debug.WriteLine($"toTable={r("table")}  fromCol={r("from")}  toCol={r("to")}  onDelete={r("on_delete")}")
                    End While
                End Using
            End Using
        End Sub
        Private Async Sub Exit_Click(sender As Object, e As RoutedEventArgs) Handles ExitButton.Click
            ' If Open All is running, stop it first (installer-friendly)
            If _isOpeningAll Then
                Try
                    _openAllCts?.Cancel()
                Catch
                End Try

                StatusText.Text = "Stopping Open All…"
                Await Task.Delay(150) ' brief yield so cancellation can propagate
            End If

            ' Decide which scenario we are in
            Dim workflowComplete As Boolean = (_confirmed OrElse _saved)

            If Not workflowComplete Then
                ' Scenario 1: leaving early => commissioning failed / incomplete
                Dim result = MessageBox.Show(
            "Locker commissioning is not complete (not confirmed/saved)." & Environment.NewLine &
            "Yes = go back to Controller Assignment" & Environment.NewLine &
            "No = exit commissioning",
            "Exit Locker Commissioning",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning)

                If result = MessageBoxResult.Cancel Then Return

                If result = MessageBoxResult.Yes Then
                    ' Step back in workflow
                    RaiseEvent ExitRequested(CommissioningExitMode.BackToControllerAssignment)
                    Me.Close()
                    Return
                Else
                    ' Abort commissioning completely
                    RaiseEvent ExitRequested(CommissioningExitMode.AbortCommissioning)
                    Me.Close()
                    Return
                End If
            Else
                ' Scenario 2: confirmed/saved => proceed to next step
                Dim result = MessageBox.Show(
            "Proceed to the next commissioning step?",
            "Continue",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question)

                If result <> MessageBoxResult.OK Then Return

                RaiseEvent ExitRequested(CommissioningExitMode.ContinueToNextStep)
                Me.Close()
            End If
            HighlightStep(5)
        End Sub
        Private Sub HighlightStep(stepNumber As Integer)

            ' Reset all to normal
            Step1Card.Style = CType(Me.Resources("StepCardStyle"), Style)
            Step2Card.Style = CType(Me.Resources("StepCardStyle"), Style)
            Step3Card.Style = CType(Me.Resources("StepCardStyle"), Style)
            Step4Card.Style = CType(Me.Resources("StepCardStyle"), Style)
            Step5Card.Style = CType(Me.Resources("StepCardStyle"), Style)

            ' Apply highlight to selected step
            Select Case stepNumber
                Case 1
                    Step1Card.Style = CType(Me.Resources("ActiveStepCardStyle"), Style)
                Case 2
                    Step2Card.Style = CType(Me.Resources("ActiveStepCardStyle"), Style)
                Case 3
                    Step3Card.Style = CType(Me.Resources("ActiveStepCardStyle"), Style)
                Case 4
                    Step4Card.Style = CType(Me.Resources("ActiveStepCardStyle"), Style)
                Case 5
                    Step5Card.Style = CType(Me.Resources("ActiveStepCardStyle"), Style)
            End Select

        End Sub
        Private Function BuildAttachedBoards(allCells As IList(Of RelayCellVm)) As ObservableCollection(Of ObservableCollection(Of RelayCellVm))
            Dim result As New ObservableCollection(Of ObservableCollection(Of RelayCellVm))()

            ' group by board index 0..24 based on RelayId (1..200)
            For boardIndex As Integer = 0 To Boards - 1
                Dim startRelay As Integer = boardIndex * PortsPerBoard + 1
                Dim endRelay As Integer = startRelay + PortsPerBoard - 1

                Dim boardCells = allCells.
                    Where(Function(c) c IsNot Nothing AndAlso c.RelayId >= startRelay AndAlso c.RelayId <= endRelay AndAlso c.IsAttached).
                    OrderBy(Function(c) c.RelayId).
                    ToList()

                If boardCells.Count > 0 Then
                    result.Add(New ObservableCollection(Of RelayCellVm)(boardCells))
                End If
            Next

            Return result
        End Function
        Private Sub UpdateVisibleBoards(allCells As ObservableCollection(Of RelayCellVm),
                                targetBoards As ObservableCollection(Of ObservableCollection(Of RelayCellVm)))

            Dim boards = BuildAttachedBoards(allCells) ' returns ObservableCollection(Of ObservableCollection(Of RelayCellVm))

            targetBoards.Clear()
            For Each row In boards
                targetBoards.Add(row)
            Next
        End Sub
    End Class




    Public Class LockerCommissioningVm
        Implements INotifyPropertyChanged

        Private Const TotalRelays As Integer = 200
        Private Const Boards As Integer = 25
        Private Const PortsPerBoard As Integer = 8

        ' Flat backing collections (always 200 items each)
        Public ReadOnly Property BranchAAll As New ObservableCollection(Of RelayCellVm)()
        Public ReadOnly Property BranchBAll As New ObservableCollection(Of RelayCellVm)()

        ' What the UI should bind to (changes after OpenAll / filter)
        Public ReadOnly Property BranchAVisibleBoards As New ObservableCollection(Of ObservableCollection(Of RelayCellVm))()
        Public ReadOnly Property BranchBVisibleBoards As New ObservableCollection(Of ObservableCollection(Of RelayCellVm))()

        ' Optional “current view” flat lists (your FindCell uses these)
        Private _branchACells As ObservableCollection(Of RelayCellVm)
        Public Property BranchACells As ObservableCollection(Of RelayCellVm)
            Get
                Return _branchACells
            End Get
            Set(value As ObservableCollection(Of RelayCellVm))
                _branchACells = value
                OnPropertyChanged()
            End Set
        End Property

        Private _branchBCells As ObservableCollection(Of RelayCellVm)
        Public Property BranchBCells As ObservableCollection(Of RelayCellVm)
            Get
                Return _branchBCells
            End Get
            Set(value As ObservableCollection(Of RelayCellVm))
                _branchBCells = value
                OnPropertyChanged()
            End Set
        End Property

        Private _showAttachedOnly As Boolean
        Public Property ShowAttachedOnly As Boolean
            Get
                Return _showAttachedOnly
            End Get
            Set(value As Boolean)
                If _showAttachedOnly <> value Then
                    _showAttachedOnly = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _branchATitle As String = "Branch A"
        Public Property BranchATitle As String
            Get
                Return _branchATitle
            End Get
            Set(value As String)
                If _branchATitle <> value Then
                    _branchATitle = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _branchBTitle As String = "Branch B"
        Public Property BranchBTitle As String
            Get
                Return _branchBTitle
            End Get
            Set(value As String)
                If _branchBTitle <> value Then
                    _branchBTitle = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _attachedCountA As Integer
        Public Property AttachedCountA As Integer
            Get
                Return _attachedCountA
            End Get
            Set(value As Integer)
                If _attachedCountA <> value Then
                    _attachedCountA = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _attachedCountB As Integer
        Public Property AttachedCountB As Integer
            Get
                Return _attachedCountB
            End Get
            Set(value As Integer)
                If _attachedCountB <> value Then
                    _attachedCountB = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _branchACountLine As String = ""
        Public Property BranchACountLine As String
            Get
                Return _branchACountLine
            End Get
            Set(value As String)
                If _branchACountLine <> value Then
                    _branchACountLine = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _branchBCountLine As String = ""
        Public Property BranchBCountLine As String
            Get
                Return _branchBCountLine
            End Get
            Set(value As String)
                If _branchBCountLine <> value Then
                    _branchBCountLine = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        ' ------------------------
        ' Initialization
        ' ------------------------
        Public Sub InitCells()
            BranchAAll.Clear()
            BranchBAll.Clear()

            For relayId As Integer = 1 To TotalRelays
                BranchAAll.Add(New RelayCellVm(relayId) With {.Branch = "A"})
                BranchBAll.Add(New RelayCellVm(relayId) With {.Branch = "B"})
            Next

            ' Flat “view” lists for helper FindCell()
            BranchACells = New ObservableCollection(Of RelayCellVm)(BranchAAll)
            BranchBCells = New ObservableCollection(Of RelayCellVm)(BranchBAll)

            ' Default visible boards: full 25×8
            RebuildVisibleBoards(attachedOnly:=False)

            RefreshCountLines()
        End Sub

        Public Sub RefreshCountLines()
            BranchACountLine = $"A: Attached={AttachedCountA} / {TotalRelays}"
            BranchBCountLine = $"B: Attached={AttachedCountB} / {TotalRelays}"
        End Sub

        ' ------------------------
        ' View switching
        ' ------------------------
        Public Sub RebuildVisibleBoards(attachedOnly As Boolean)
            ShowAttachedOnly = attachedOnly

            BuildBoardsFromFlat(BranchAAll, BranchAVisibleBoards, attachedOnly)

            ' NOTE: caller can decide whether B should be empty based on CanScanBranch("B")
            BuildBoardsFromFlat(BranchBAll, BranchBVisibleBoards, attachedOnly)

            ' If you want FindCell to still work fast on only-attached, update flat views too:
            If attachedOnly Then
                BranchACells = New ObservableCollection(Of RelayCellVm)(BranchAAll.Where(Function(c) c.IsAttached))
                BranchBCells = New ObservableCollection(Of RelayCellVm)(BranchBAll.Where(Function(c) c.IsAttached))
            Else
                BranchACells = New ObservableCollection(Of RelayCellVm)(BranchAAll)
                BranchBCells = New ObservableCollection(Of RelayCellVm)(BranchBAll)
            End If
        End Sub

        Private Sub BuildBoardsFromFlat(flat As ObservableCollection(Of RelayCellVm),
                                   targetBoards As ObservableCollection(Of ObservableCollection(Of RelayCellVm)),
                                   attachedOnly As Boolean)

            targetBoards.Clear()

            If Not attachedOnly Then
                ' Full fixed 25×8
                For boardIdx As Integer = 0 To Boards - 1
                    Dim row As New ObservableCollection(Of RelayCellVm)()
                    For portIdx As Integer = 0 To PortsPerBoard - 1
                        Dim idx As Integer = boardIdx * PortsPerBoard + portIdx
                        row.Add(flat(idx))
                    Next
                    targetBoards.Add(row)
                Next
                Return
            End If

            ' Attached-only: make rows of 8 from attached cells in RelayId order
            Dim attached = flat.Where(Function(c) c.IsAttached).OrderBy(Function(c) c.RelayId).ToList()

            Dim i As Integer = 0
            While i < attached.Count
                Dim row As New ObservableCollection(Of RelayCellVm)()
                For k As Integer = 1 To PortsPerBoard
                    If i >= attached.Count Then Exit For
                    row.Add(attached(i))
                    i += 1
                Next
                targetBoards.Add(row)
            End While
        End Sub

        ' ------------------------
        ' INotifyPropertyChanged
        ' ------------------------
        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Protected Sub OnPropertyChanged(<CallerMemberName> Optional prop As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(prop))
        End Sub
    End Class

    Public Class RelayCellVm
        Implements INotifyPropertyChanged

        Public Sub New(relayId As Integer)
            Me.RelayId = relayId
            Me.Label = relayId.ToString()
            Me.CellBrush = Brushes.LightGray
            Me.AssignedBrush = Brushes.LightGreen   ' choose whatever color you like
        End Sub

        Public Property Branch As String   ' "A" or "B"

        Public ReadOnly Property RelayId As Integer

        ' --------------------------
        ' Attached / Open state
        ' --------------------------
        Private _isAttached As Boolean
        Public Property IsAttached As Boolean
            Get
                Return _isAttached
            End Get
            Set(value As Boolean)
                If _isAttached <> value Then
                    _isAttached = value
                    OnPropertyChanged()
                    ' If your UI color depends on IsAttached and you set CellBrush elsewhere, no action needed.
                    ' If you want IsAttached itself to affect DisplayBrush, you may want to refresh here.
                    OnPropertyChanged(NameOf(DisplayBrush))
                End If
            End Set
        End Property

        Private _isOpen As Boolean
        Public Property IsOpen As Boolean
            Get
                Return _isOpen
            End Get
            Set(value As Boolean)
                If _isOpen <> value Then
                    _isOpen = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        ' --------------------------
        ' Assignment state (NEW)
        ' --------------------------
        Private _isAssigned As Boolean
        Public Property IsAssigned As Boolean
            Get
                Return _isAssigned
            End Get
            Set(value As Boolean)
                If _isAssigned <> value Then
                    _isAssigned = value
                    OnPropertyChanged()
                    OnPropertyChanged(NameOf(DisplayBrush))
                    UpdateLabelForAssignment()
                End If
            End Set
        End Property

        Private _assignedLockerNumber As String
        Public Property AssignedLockerNumber As String
            Get
                Return _assignedLockerNumber
            End Get
            Set(value As String)
                If _assignedLockerNumber <> value Then
                    _assignedLockerNumber = value
                    OnPropertyChanged()
                    UpdateLabelForAssignment()
                End If
            End Set
        End Property

        ' Brush used when assigned (you can change this per-state if desired)
        Private _assignedBrush As Brush
        Public Property AssignedBrush As Brush
            Get
                Return _assignedBrush
            End Get
            Set(value As Brush)
                If _assignedBrush IsNot value Then
                    _assignedBrush = value
                    OnPropertyChanged()
                    OnPropertyChanged(NameOf(DisplayBrush))
                End If
            End Set
        End Property

        Private Sub UpdateLabelForAssignment()
            ' If assigned, show locker number as the label (your request)
            If IsAssigned AndAlso Not String.IsNullOrWhiteSpace(AssignedLockerNumber) Then
                Label = AssignedLockerNumber
            Else
                ' Default label back to relay id
                Label = RelayId.ToString()
            End If
        End Sub

        ' --------------------------
        ' Label + Brushes
        ' --------------------------
        Private _label As String
        Public Property Label As String
            Get
                Return _label
            End Get
            Set(value As String)
                If _label <> value Then
                    _label = value
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _cellBrush As Brush
        Public Property CellBrush As Brush
            Get
                Return _cellBrush
            End Get
            Set(value As Brush)
                If _cellBrush IsNot value Then
                    _cellBrush = value
                    OnPropertyChanged()
                    OnPropertyChanged(NameOf(DisplayBrush))
                End If
            End Set
        End Property

        ' Transient overlay brush for "attempt" flash (already in your code)
        Private _flashBrush As Brush
        Public Property FlashBrush As Brush
            Get
                Return _flashBrush
            End Get
            Set(value As Brush)
                If _flashBrush IsNot value Then
                    _flashBrush = value
                    OnPropertyChanged()
                    OnPropertyChanged(NameOf(DisplayBrush))
                End If
            End Set
        End Property

        ' What the UI binds to:
        ' Priority: Flash > Assigned > CellBrush
        Public ReadOnly Property DisplayBrush As Brush
            Get
                If _flashBrush IsNot Nothing Then Return _flashBrush
                If IsAssigned Then Return If(_assignedBrush, Brushes.LightGreen)
                Return _cellBrush
            End Get
        End Property

        ' --------------------------
        ' INotifyPropertyChanged
        ' --------------------------
        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Protected Sub OnPropertyChanged(<CallerMemberName> Optional prop As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(prop))
        End Sub
    End Class

End Namespace
