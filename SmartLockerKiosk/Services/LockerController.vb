Imports System.Collections.Concurrent
Imports HldSerialLib.Serial.LockBoard
Imports Microsoft.EntityFrameworkCore

Public Class LockerControllerService
    Implements IDisposable

    Private ReadOnly _boards As New Dictionary(Of String, HldLockBoard)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _locks As New ConcurrentDictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _initGate As New Object()
    Private _initialized As Boolean = False
    Private ReadOnly _lastUnlockUtc As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)

    Public Sub Initialize()
        Dim openedAny As Boolean = False

        SyncLock _initGate
            ' Idempotent: don't reopen if already initialized
            If _initialized Then Return

            Using db = DatabaseBootstrapper.BuildDbContext()

                Dim ports = db.ControllerPorts.AsNoTracking().
                Where(Function(p) p.IsEnabled AndAlso
                                  Not String.IsNullOrWhiteSpace(p.BranchName) AndAlso
                                  Not String.IsNullOrWhiteSpace(p.PortName)).
                ToList()

                ' Normalize
                Dim normalized = ports.Select(Function(p) New With {
                .Branch = p.BranchName.Trim().ToUpperInvariant(),
                .Port = p.PortName.Trim()
            }).ToList()

                ' Guard: duplicate COM ports across branches
                Dim dupPorts = normalized.
                GroupBy(Function(x) x.Port, StringComparer.OrdinalIgnoreCase).
                Where(Function(g) g.Count() > 1).
                Select(Function(g) g.Key).
                ToList()

                If dupPorts.Count > 0 Then
                    Throw New InvalidOperationException(
                    $"Duplicate COM port assignment in ControllerPorts: {String.Join(", ", dupPorts)}")
                End If

                ' Open each branch, but never crash whole init on one busy port
                For Each m In normalized
                    Dim branch = m.Branch
                    Dim portName = m.Port

                    If _boards.ContainsKey(branch) Then Continue For

                    Dim board As HldLockBoard = Nothing
                    Try
                        board = New HldLockBoard()
                        board.Open(portName, 115200)

                        ' Give device time after open; avoids immediate query issues
                        Threading.Thread.Sleep(200)

                        ' Optional probe (do not fail init if probe fails)
                        Try
                            board.GetLockStatus(1)
                        Catch ex As Exception
                            Debug.WriteLine($"WARN: {branch} opened on {portName} but GetLockStatus failed: {ex.GetType().Name} - {ex.Message}")
                        End Try

                        _boards(branch) = board
                        _locks(branch) = New Object()
                        openedAny = True

                        board = Nothing ' ownership transferred to _boards

                    Catch ex As UnauthorizedAccessException
                        ' Port busy/denied: skip this branch, keep going
                        Debug.WriteLine($"WARN: {branch} {portName} denied/busy: {ex.Message}")

                    Catch ex As Exception
                        ' Any other failure: skip this branch
                        Debug.WriteLine($"WARN: {branch} {portName} open failed: {ex.GetType().Name} - {ex.Message}")

                    Finally
                        If board IsNot Nothing Then
                            Try : board.Close() : Catch : End Try
                        End If
                    End Try
                Next

            End Using

            ' Only mark initialized if we actually opened at least one controller
            _initialized = openedAny
        End SyncLock
    End Sub
    Public Sub UnlockLocker(lockerId As Integer)
        Try
            EnsureInitialized()
        Catch ex As Exception
            Throw New InvalidOperationException("Locker controller is unavailable. Please contact attendant.", ex)
        End Try

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.Single(Function(l) l.LockerId = lockerId)

            Dim branch = NormalizeBranch(locker.Branch)
            Dim relayId = locker.RelayId

            Dim board = GetBoardOrThrow(branch)
            Dim gate = GetBranchLockObject(branch)

            SyncLock gate
                Try
                    EnforceUnlockInterval(branch)
                    board.Unlock(relayId)
                Catch ex As NullReferenceException
                    ' Vendor SDK internal NRE => treat as comms/protocol failure
                    Throw New InvalidOperationException($"Controller call failed (SDK null ref) for Branch {branch}, Relay {relayId}. Check comms/cable/board state.", ex)
                End Try
            End SyncLock
        End Using
    End Sub
    Public Function GetLockStatus(lockerId As Integer) As Integer
        Try
            EnsureInitialized()
        Catch ex As Exception
            Throw New InvalidOperationException("Locker controller is unavailable. Please contact attendant.", ex)
        End Try

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.Single(Function(l) l.LockerId = lockerId)

            Dim branch = NormalizeBranch(locker.Branch)
            Dim relayId = locker.RelayId

            Dim board = GetBoardOrThrow(branch)
            Dim gate = GetBranchLockObject(branch)

            SyncLock gate
                Try
                    Return GetLockStatus(relayId)
                Catch ex As NullReferenceException
                    Throw New InvalidOperationException($"GetLockStatus failed (SDK null ref) for Branch {branch}, Relay {relayId}.", ex)
                End Try
            End SyncLock
        End Using
    End Function
    Public Function GetCompartmentStatus(lockerId As Integer) As Integer
        Try
            EnsureInitialized()
        Catch ex As Exception
            Throw New InvalidOperationException("Locker controller is unavailable. Please contact attendant.", ex)
        End Try

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.Single(Function(l) l.LockerId = lockerId)

            Dim branch = NormalizeBranch(locker.Branch)
            Dim relayId = locker.RelayId

            Dim board = GetBoardOrThrow(branch)
            Dim gate = GetBranchLockObject(branch)

            SyncLock gate
                Try
                    Return board.GetSensorStatus(relayId)
                Catch ex As NullReferenceException
                    Throw New InvalidOperationException($"GetSensorStatus failed (SDK null ref) for Branch {branch}, Relay {relayId}.", ex)
                End Try
            End SyncLock
        End Using
    End Function
    Public Sub UnlockByLockerNumber(lockerNumber As String)
        Try
            EnsureInitialized()
        Catch ex As Exception
            Throw New InvalidOperationException("Locker controller is unavailable. Please contact attendant.", ex)
        End Try

        If String.IsNullOrWhiteSpace(lockerNumber) Then
            Throw New ArgumentException("lockerNumber is required.", NameOf(lockerNumber))
        End If

        Dim ln = lockerNumber.Trim()

        Using db = DatabaseBootstrapper.BuildDbContext()
            Dim locker = db.Lockers.AsNoTracking().
            SingleOrDefault(Function(l) l.LockerNumber = ln)

            If locker Is Nothing Then
                Throw New InvalidOperationException($"LockerNumber '{ln}' not found in local DB.")
            End If

            ' Avoid double-db-open by calling internal helper:
            UnlockByBranchAndRelay(NormalizeBranch(locker.Branch), locker.RelayId)
        End Using
    End Sub
    Private Sub UnlockByBranchAndRelay(branch As String, relayId As Integer)
        Try
            EnsureInitialized()
        Catch ex As Exception
            Throw New InvalidOperationException("Locker controller is unavailable. Please contact attendant.", ex)
        End Try

        Dim board = GetBoardOrThrow(branch)
        Dim gate = GetBranchLockObject(branch)

        SyncLock gate
            Try
                EnforceUnlockInterval(branch)
                board.Unlock(relayId)
            Catch ex As NullReferenceException
                Throw New InvalidOperationException($"Unlock failed (SDK null ref) for Branch {branch}, Relay {relayId}.", ex)
            End Try
        End SyncLock
    End Sub
    Private Sub EnsureInitialized()
        If _initialized Then Return
        Initialize()
    End Sub
    Private Function NormalizeBranch(branch As String) As String
        Return (If(branch, "")).Trim().ToUpperInvariant()
    End Function
    Private Function GetBranchLockObject(branch As String) As Object
        Dim gate As Object = Nothing
        If Not _locks.TryGetValue(branch, gate) OrElse gate Is Nothing Then
            gate = New Object()
            _locks(branch) = gate
        End If
        Return gate
    End Function
    Private Function GetBoardOrThrow(branch As String) As HldLockBoard
        Dim b = NormalizeBranch(branch)
        If String.IsNullOrWhiteSpace(b) Then
            Throw New InvalidOperationException("Branch is blank.")
        End If

        Dim board As HldLockBoard = Nothing
        If Not _boards.TryGetValue(b, board) OrElse board Is Nothing Then
            Throw New InvalidOperationException($"No controller configured/initialized for Branch {b}.")
        End If

        Return board
    End Function
    Private Sub EnforceUnlockInterval(branch As String)
        ' Doc: interval between two unlockings >= 600ms
        Dim now = DateTime.UtcNow
        Dim last = _lastUnlockUtc.GetOrAdd(branch, DateTime.MinValue)

        Dim elapsedMs = (now - last).TotalMilliseconds
        If elapsedMs < 600 Then
            Threading.Thread.Sleep(CInt(600 - elapsedMs))
        End If

        _lastUnlockUtc(branch) = DateTime.UtcNow
    End Sub
    Private Sub CloseAllBoards()
        For Each kvp In _boards.ToList()
            Try
                kvp.Value.Close()
            Catch
                ' ignore
            End Try
        Next

        _boards.Clear()
        _locks.Clear()
        _initialized = False
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _initGate
            CloseAllBoards()
        End SyncLock
    End Sub

End Class

