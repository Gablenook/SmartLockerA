Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Public Class LockerActionService

    Public Async Function CreateJournalEntryAsync(request As LockerActionRequest) As Task(Of LockerTransactionJournal)

        If request Is Nothing Then
            Throw New ArgumentNullException(NameOf(request))
        End If

        request.Validate()

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal As New LockerTransactionJournal With {
                .RequestId = Guid.NewGuid().ToString("N"),
                .TransactionId = request.TransactionId,
                .CommandId = request.CommandId,
                .CorrelationId = request.CorrelationId,
                .KioskId = AppSettings.KioskID,
                .LockerId = request.LockerId,
                .LockerNumber = request.LockerNumber,
                .Branch = request.Branch,
                .RelayId = request.RelayId,
                .Workflow = request.Workflow,
                .ActionType = request.ActionType,
                .ActorId = request.ActorId,
                .Credential = request.Credential,
                .AssetTag = request.AssetTag,
                .DeviceType = request.DeviceType,
                .TransactionState = LockerTransactionState.Created,
                .AckStatus = If(request.RequiresBackendAck,
                                LockerAckStatus.Pending,
                                LockerAckStatus.NotRequired),
                .CreatedUtc = DateTime.UtcNow,
                .UpdatedUtc = DateTime.UtcNow
            }

            db.LockerTransactionJournals.Add(journal)

            Await db.SaveChangesAsync()

            Return journal

        End Using

    End Function
    Public Async Function UpdateJournalStateAsync(journalId As Integer,
                                              newState As LockerTransactionState,
                                              Optional ackStatus As LockerAckStatus? = Nothing,
                                              Optional errorMessage As String = Nothing,
                                              Optional responseJson As String = Nothing) As Task

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                SingleOrDefaultAsync(Function(x) x.Id = journalId)

            If journal Is Nothing Then
                Throw New InvalidOperationException($"Locker transaction journal row {journalId} was not found.")
            End If

            journal.TransactionState = newState
            journal.UpdatedUtc = DateTime.UtcNow

            If ackStatus.HasValue Then
                journal.AckStatus = ackStatus.Value
            End If

            If Not String.IsNullOrWhiteSpace(errorMessage) Then
                journal.LastError = errorMessage
            End If

            If Not String.IsNullOrWhiteSpace(responseJson) Then
                journal.ResponseJson = responseJson
            End If

            If newState = LockerTransactionState.AckSucceeded OrElse
               newState = LockerTransactionState.Cancelled Then

                journal.CompletedUtc = DateTime.UtcNow

            End If

            Await db.SaveChangesAsync()

        End Using

    End Function
    Public Async Function ExecuteLockerActionAsync(request As LockerActionRequest,
                                               openDoorAsync As Func(Of Task)) As Task(Of LockerActionResult)

        If request Is Nothing Then
            Throw New ArgumentNullException(NameOf(request))
        End If

        If openDoorAsync Is Nothing Then
            Throw New ArgumentNullException(NameOf(openDoorAsync))
        End If

        Dim journal As LockerTransactionJournal = Nothing
        Dim caughtException As Exception = Nothing

        Try
            journal = Await CreateJournalEntryAsync(request)

            Await UpdateJournalStateAsync(journal.Id,
                                      LockerTransactionState.DoorOpenRequested)

            Await openDoorAsync()

            Await UpdateJournalStateAsync(journal.Id,
                                      LockerTransactionState.DoorOpened)

            Return LockerActionResult.Ok(journal, "Door open command completed.")

        Catch ex As Exception

            caughtException = ex

        End Try

        ' VB.NET allows Await here because we are now outside the Catch block.
        If caughtException IsNot Nothing Then

            If journal IsNot Nothing Then

                Try
                    Await UpdateJournalStateAsync(
                    journal.Id,
                    LockerTransactionState.NeedsReconciliation,
                    LockerAckStatus.Failed,
                    caughtException.Message)

                Catch updateEx As Exception

                    TraceLogger.Log(
                    $"Failed to update journal state after exception. " &
                    $"JournalId={journal.Id} Error={updateEx}")

                End Try

                Return LockerActionResult.Fail(journal, caughtException.Message)

            End If

            Throw caughtException

        End If

        Throw New InvalidOperationException("ExecuteLockerActionAsync ended unexpectedly.")

    End Function

    Public Function BuildRequestFromLocker(locker As Locker,
                                       workflow As String,
                                       actionType As String,
                                       Optional actorId As String = Nothing,
                                       Optional credential As String = Nothing,
                                       Optional assetTag As String = Nothing,
                                       Optional deviceType As String = Nothing,
                                       Optional transactionId As String = Nothing,
                                       Optional commandId As String = Nothing,
                                       Optional correlationId As String = Nothing,
                                       Optional requiresBackendAck As Boolean = True) As LockerActionRequest

        If locker Is Nothing Then
            Throw New ArgumentNullException(NameOf(locker))
        End If

        Return New LockerActionRequest With {
            .Workflow = workflow,
            .ActionType = actionType,
            .LockerId = locker.LockerId,
            .LockerNumber = locker.LockerNumber,
            .Branch = locker.Branch,
            .RelayId = locker.RelayId,
            .ActorId = actorId,
            .Credential = credential,
            .AssetTag = assetTag,
            .DeviceType = deviceType,
            .TransactionId = transactionId,
            .CommandId = commandId,
            .CorrelationId = correlationId,
            .RequiresBackendAck = requiresBackendAck
        }

    End Function
    Public Async Function MarkDoorClosedAndLocalStateUpdatedAsync(journalId As Integer,
                                                              Optional responseJson As String = Nothing) As Task

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                SingleOrDefaultAsync(Function(x) x.Id = journalId)

            If journal Is Nothing Then
                Throw New InvalidOperationException($"Locker transaction journal row {journalId} was not found.")
            End If

            journal.TransactionState = LockerTransactionState.LocalStateUpdated
            journal.UpdatedUtc = DateTime.UtcNow

            If Not String.IsNullOrWhiteSpace(responseJson) Then
                journal.ResponseJson = responseJson
            End If

            If journal.AckStatus = LockerAckStatus.Pending Then
                ' Leave it pending so the sync/ACK service can send it.
            ElseIf journal.AckStatus = LockerAckStatus.NotRequired Then
                journal.CompletedUtc = DateTime.UtcNow
            End If

            Await db.SaveChangesAsync()

        End Using

    End Function
    Public Async Function MarkAckPendingAsync(journalId As Integer,
                                          Optional requestJson As String = Nothing) As Task

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                SingleOrDefaultAsync(Function(x) x.Id = journalId)

            If journal Is Nothing Then
                Throw New InvalidOperationException($"Locker transaction journal row {journalId} was not found.")
            End If

            journal.TransactionState = LockerTransactionState.AckPending
            journal.AckStatus = LockerAckStatus.Pending
            journal.UpdatedUtc = DateTime.UtcNow

            If Not String.IsNullOrWhiteSpace(requestJson) Then
                journal.RequestJson = requestJson
            End If

            Await db.SaveChangesAsync()

        End Using

    End Function
    Public Async Function MarkAckSucceededAsync(journalId As Integer,
                                                Optional responseJson As String = Nothing) As Task

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                SingleOrDefaultAsync(Function(x) x.Id = journalId)

            If journal Is Nothing Then
                Throw New InvalidOperationException($"Locker transaction journal row {journalId} was not found.")
            End If

            journal.TransactionState = LockerTransactionState.AckSucceeded
            journal.AckStatus = LockerAckStatus.Succeeded
            journal.UpdatedUtc = DateTime.UtcNow
            journal.CompletedUtc = DateTime.UtcNow
            journal.LastError = Nothing

            If Not String.IsNullOrWhiteSpace(responseJson) Then
                journal.ResponseJson = responseJson
            End If

            Await db.SaveChangesAsync()

        End Using

    End Function
    Public Async Function MarkAckFailedAsync(journalId As Integer,
                                         errorMessage As String,
                                         Optional responseJson As String = Nothing) As Task

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                SingleOrDefaultAsync(Function(x) x.Id = journalId)

            If journal Is Nothing Then
                Throw New InvalidOperationException($"Locker transaction journal row {journalId} was not found.")
            End If

            journal.TransactionState = LockerTransactionState.AckFailed
            journal.AckStatus = LockerAckStatus.Failed
            journal.RetryCount += 1
            journal.LastAttemptUtc = DateTime.UtcNow
            journal.UpdatedUtc = DateTime.UtcNow
            journal.LastError = errorMessage

            If Not String.IsNullOrWhiteSpace(responseJson) Then
                journal.ResponseJson = responseJson
            End If

            Await db.SaveChangesAsync()

        End Using

    End Function
    Public Async Function GetPendingAckTransactionsAsync(Optional maxCount As Integer = 25) _
    As Task(Of List(Of LockerTransactionJournal))

        Using db = DatabaseBootstrapper.BuildDbContext()

            Return Await db.LockerTransactionJournals.
                Where(Function(x) x.AckStatus = LockerAckStatus.Pending OrElse
                                  x.AckStatus = LockerAckStatus.Failed).
                Where(Function(x) x.TransactionState = LockerTransactionState.AckPending OrElse
                                  x.TransactionState = LockerTransactionState.AckFailed OrElse
                                  x.TransactionState = LockerTransactionState.LocalStateUpdated).
                OrderBy(Function(x) x.CreatedUtc).
                Take(maxCount).
                ToListAsync()

        End Using

    End Function
    Public Async Function ExecuteAdminStateChangeAsync(request As LockerActionRequest,
                                                   stateChangeAsync As Func(Of Task)) As Task(Of LockerActionResult)

        If request Is Nothing Then Throw New ArgumentNullException(NameOf(request))
        If stateChangeAsync Is Nothing Then Throw New ArgumentNullException(NameOf(stateChangeAsync))

        request.RequiresBackendAck = False

        Dim journal As LockerTransactionJournal = Nothing
        Dim caughtException As Exception = Nothing

        Try
            journal = Await CreateJournalEntryAsync(request)

            Await stateChangeAsync()

            Await UpdateJournalStateAsync(
                journal.Id,
                LockerTransactionState.LocalStateUpdated,
                LockerAckStatus.NotRequired)

            Await UpdateJournalStateAsync(
                journal.Id,
                LockerTransactionState.AckSucceeded,
                LockerAckStatus.NotRequired)

            Return LockerActionResult.Ok(journal, "Admin state change completed.")

        Catch ex As Exception
            caughtException = ex
        End Try

        If caughtException IsNot Nothing Then

            If journal IsNot Nothing Then
                Await UpdateJournalStateAsync(
                    journal.Id,
                    LockerTransactionState.NeedsReconciliation,
                    LockerAckStatus.Failed,
                    caughtException.Message)

                Return LockerActionResult.Fail(journal, caughtException.Message)
            End If

            Throw caughtException
        End If

        Throw New InvalidOperationException("ExecuteAdminStateChangeAsync ended unexpectedly.")

    End Function

End Class
