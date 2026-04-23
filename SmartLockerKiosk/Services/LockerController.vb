Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports Microsoft.EntityFrameworkCore

Public Class LockerControllerService
    Implements IDisposable

    Public Event BranchStatusUpdated(branch As String)
    Public Event Trace(branch As String, message As String)
    Public Event Disconnected(branch As String, reason As String)
    Public Event Reconnected(branch As String, portName As String)

    Private ReadOnly _initGate As New Object()
    Private ReadOnly _sessions As New Dictionary(Of String, BranchSession)(StringComparer.OrdinalIgnoreCase)



    Private Class BranchSession
        Public ReadOnly Branch As String
        Public ReadOnly PortName As String
        Public ReadOnly Controller As HldRelayController.HldRelayController
        Public HasFirstFrame As Boolean
        Public LastUnlockUtc As DateTime = DateTime.MinValue
        Public ReadOnly UnlockGate As New Object()

        Public Sub New(branch As String, portName As String, controller As HldRelayController.HldRelayController)
            Me.Branch = branch
            Me.PortName = portName
            Me.Controller = controller
        End Sub
    End Class

    ' ---------- Public API ----------
    Public Sub InitializeFromDb()
        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim ports = db.ControllerPorts.AsNoTracking().
                Where(Function(p) p.IsEnabled AndAlso
                                  Not String.IsNullOrWhiteSpace(p.BranchName) AndAlso
                                  Not String.IsNullOrWhiteSpace(p.PortName)).
                ToList()

            Dim normalized = ports.Select(Function(p) New With {
                .Branch = NormalizeBranch(p.BranchName),
                .Port = p.PortName.Trim()
            }).ToList()

            Dim dupPorts = normalized.
                GroupBy(Function(x) x.Port, StringComparer.OrdinalIgnoreCase).
                Where(Function(g) g.Count() > 1).
                Select(Function(g) g.Key).
                ToList()

            If dupPorts.Count > 0 Then
                Throw New InvalidOperationException($"Duplicate COM port assignment: {String.Join(", ", dupPorts)}")
            End If

            For Each m In normalized
                ConnectBranch(m.Branch, m.Port)
            Next
        End Using
    End Sub
    Public Sub ConnectBranch(branch As String)
        Dim b = NormalizeBranch(branch)
        If String.IsNullOrWhiteSpace(b) Then Throw New ArgumentException("branch is required.", NameOf(branch))

        Dim portName As String = Nothing
        Dim enabled As Boolean = False

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim row = db.ControllerPorts.AsNoTracking().SingleOrDefault(Function(r) r.BranchName = b)
            portName = row?.PortName
            enabled = (row IsNot Nothing AndAlso row.IsEnabled)
        End Using

        If Not enabled Then Throw New InvalidOperationException($"Branch {b} is not enabled in DB.")
        If String.IsNullOrWhiteSpace(portName) Then Throw New InvalidOperationException($"Branch {b} has no PortName in DB.")

        ConnectBranch(b, portName)
    End Sub
    Public Function ConnectBranch(branch As String, portName As String) As Boolean
        Dim b = NormalizeBranch(branch)

        If String.IsNullOrWhiteSpace(b) Then
            RaiseEvent Trace("", "ConnectBranch failed: branch is required.")
            Return False
        End If

        If String.IsNullOrWhiteSpace(portName) Then
            RaiseEvent Trace(b, "ConnectBranch failed: portName is required.")
            Return False
        End If

        Dim pn = portName.Trim()

        Dim ctl As HldRelayController.HldRelayController = Nothing
        Dim s As BranchSession = Nothing

        ' Create + register under lock, but do not Start under lock.
        SyncLock _initGate
            If _sessions.ContainsKey(b) Then
                RaiseEvent Trace(b, $"Branch {b} is already connected.")
                Return True
            End If

            ctl = New HldRelayController.HldRelayController(autoReconnect:=True)
            s = New BranchSession(b, pn, ctl)

            AddHandler ctl.StatusUpdated,
            Sub()
                s.HasFirstFrame = True
                RaiseEvent BranchStatusUpdated(b)
            End Sub

            AddHandler ctl.Trace,
            Sub(msg As String)
                RaiseEvent Trace(b, msg)
            End Sub

            AddHandler ctl.Disconnected,
            Sub(reason As String)
                RaiseEvent Disconnected(b, reason)
            End Sub

            AddHandler ctl.Reconnected,
            Sub(p As String)
                RaiseEvent Reconnected(b, p)
            End Sub

            _sessions(b) = s
        End SyncLock

        ' Start outside lock because it may block briefly.
        Try
            ctl.Start(pn)
            RaiseEvent Trace(b, $"Connected to controller on {pn}.")
            Return True

        Catch ex As UnauthorizedAccessException
            SyncLock _initGate
                _sessions.Remove(b)
            End SyncLock

            Try : ctl.Dispose() : Catch : End Try

            RaiseEvent Trace(b, $"Port {pn} is unavailable or already in use. {ex.Message}")
            Return False

        Catch ex As IO.IOException
            SyncLock _initGate
                _sessions.Remove(b)
            End SyncLock

            Try : ctl.Dispose() : Catch : End Try

            RaiseEvent Trace(b, $"Could not open {pn}. Serial I/O error: {ex.Message}")
            Return False

        Catch ex As Exception
            SyncLock _initGate
                _sessions.Remove(b)
            End SyncLock

            Try : ctl.Dispose() : Catch : End Try

            RaiseEvent Trace(b, $"Could not connect to {pn}: {ex.Message}")
            Return False
        End Try
    End Function
    Public Function IsBranchReady(branch As String) As Boolean
        If String.IsNullOrWhiteSpace(branch) Then Return False

        Dim b = NormalizeBranch(branch)
        If String.IsNullOrWhiteSpace(b) Then Return False

        SyncLock _initGate
            Dim s As BranchSession = Nothing

            If Not _sessions.TryGetValue(b, s) OrElse s Is Nothing Then
                Return False
            End If

            If s.Controller Is Nothing Then Return False

            Return s.HasFirstFrame AndAlso
               s.Controller.LastFrameUtc <> DateTime.MinValue
        End SyncLock
    End Function
    Public Function IsBranchEnabled(branch As String) As Boolean
        If String.IsNullOrWhiteSpace(branch) Then Return False
        branch = branch.Trim().ToUpperInvariant()

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim row = db.ControllerPorts.AsNoTracking().SingleOrDefault(Function(r) r.BranchName = branch)
            Return (row IsNot Nothing AndAlso row.IsEnabled AndAlso Not String.IsNullOrWhiteSpace(row.PortName))
        End Using
    End Function
    Public Function IsCommsHealthy(branch As String) As Boolean
        Dim s = GetSessionOrThrow(branch)
        Return s.Controller.IsCommsHealthy
    End Function
    Public Function UnlockRelay(branch As String, relayId As Integer) As Boolean
        Dim s = GetSessionOrThrow(branch)

        If Not s.Controller.IsOpen Then Return False

        Dim bp = RelayIdToBoardPort(relayId)

        SyncLock s.UnlockGate
            WaitUntilUnlockAllowed(s)
            Dim ok = s.Controller.Unlock(bp.Board, bp.Port)
            ' Mark the time AFTER we actually issued the command
            s.LastUnlockUtc = DateTime.UtcNow
            Return ok
        End SyncLock
    End Function
    Public Function TryGetLockOpen(branch As String, relayId As Integer, ByRef lockOpen As Boolean) As Boolean
        lockOpen = False
        Dim s = GetSessionOrThrow(branch)

        If Not s.Controller.IsOpen Then Return False
        If Not s.HasFirstFrame Then Return False

        Dim bp = RelayIdToBoardPort(relayId)

        SyncLock s.UnlockGate   ' <-- serialize with unlocks
            Try
                lockOpen = s.Controller.GetLockOpen(bp.Board, bp.Port)
                Return True
            Catch
                Return False
            End Try
        End SyncLock
    End Function
    Public Function UnlockByLockerNumber(lockerNumber As String) As Boolean
        Dim ln = (If(lockerNumber, "")).Trim()
        If ln.Length = 0 Then Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.AsNoTracking().
            SingleOrDefault(Function(x) x.LockerNumber = ln)

            If locker Is Nothing Then
                Throw New InvalidOperationException($"Locker '{ln}' was not found.")
            End If

            If Not locker.IsEnabled Then
                Throw New InvalidOperationException($"Locker '{ln}' is disabled.")
            End If

            If String.IsNullOrWhiteSpace(locker.Branch) Then
                Throw New InvalidOperationException($"Locker '{ln}' has no branch assigned.")
            End If

            If locker.RelayId <= 0 Then
                Throw New InvalidOperationException($"Locker '{ln}' has an invalid RelayId.")
            End If

            Return UnlockRelay(locker.Branch, locker.RelayId)
        End Using
    End Function


    ' ---------- Helpers ----------
    Private Shared Function NormalizeBranch(branch As String) As String
        Return (If(branch, "")).Trim().ToUpperInvariant()
    End Function
    Private Function GetSessionOrThrow(branch As String) As BranchSession
        Dim b = NormalizeBranch(branch)
        If String.IsNullOrWhiteSpace(b) Then Throw New InvalidOperationException("Branch is blank.")

        SyncLock _initGate
            Dim s As BranchSession = Nothing
            If Not _sessions.TryGetValue(b, s) OrElse s Is Nothing Then
                Throw New InvalidOperationException($"No controller session initialized for Branch {b}.")
            End If
            Return s
        End SyncLock
    End Function
    Private Shared Sub WaitUntilUnlockAllowed(s As BranchSession)
        Dim now = DateTime.UtcNow
        Dim elapsedMs = (now - s.LastUnlockUtc).TotalMilliseconds
        If elapsedMs < 600 Then
            Threading.Thread.Sleep(CInt(Math.Ceiling(600 - elapsedMs)))
        End If
    End Sub
    Public Structure BoardPort
        Public Board As Integer
        Public Port As Integer
    End Structure
    Public Shared Function RelayIdToBoardPort(relayId As Integer) As BoardPort
        If relayId < 1 OrElse relayId > 200 Then Throw New ArgumentOutOfRangeException(NameOf(relayId), "relayId must be 1..200.")
        Dim board = ((relayId - 1) \ 8) + 1
        Dim port = ((relayId - 1) Mod 8) + 1
        Return New BoardPort With {.Board = board, .Port = port}
    End Function


    ' ---------- IDisposable ----------
    Public Sub Dispose() Implements IDisposable.Dispose
        Dim sessions As List(Of BranchSession)
        SyncLock _initGate
            sessions = _sessions.Values.ToList()
            _sessions.Clear()
        End SyncLock

        For Each s In sessions
            Try : s.Controller.Dispose() : Catch : End Try
        Next
    End Sub
    Public Function TryGetLockerMapping(lockerNumber As String, ByRef branch As String, ByRef relayId As Integer) As Boolean
        branch = Nothing
        relayId = 0

        Dim ln = (If(lockerNumber, "")).Trim()
        If ln.Length = 0 Then Return False

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.AsNoTracking().
                SingleOrDefault(Function(x) x.LockerNumber = ln)

            If locker Is Nothing Then Return False
            If Not locker.IsEnabled Then Return False
            If String.IsNullOrWhiteSpace(locker.Branch) Then Return False
            If locker.RelayId <= 0 Then Return False

            branch = locker.Branch.Trim().ToUpperInvariant()
            relayId = locker.RelayId
            Return True
        End Using
    End Function


End Class
