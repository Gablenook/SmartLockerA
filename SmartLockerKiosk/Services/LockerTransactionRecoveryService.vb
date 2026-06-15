Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.Audit

Public Class LockerTransactionRecoveryService
    Public Async Function LoadIncompleteTransactionsAsync() As Task(Of List(Of LockerTransactionJournal))

        Using db = DatabaseBootstrapper.BuildDbContext()

            Return Await db.LockerTransactionJournals _
                .Where(Function(j) j.TransactionState <> LockerTransactionState.Completed AndAlso
                                   j.TransactionState <> LockerTransactionState.Resolved AndAlso
                                   j.TransactionState <> LockerTransactionState.Cancelled) _
                .OrderByDescending(Function(j) j.CreatedUtc) _
                .ToListAsync()

        End Using

    End Function
    Public Async Function LoadIncompleteTransactionRowsAsync(
        Optional takeCount As Integer = 200
    ) As Task(Of List(Of IncompleteTransactionRow))

        Dim journals = Await GetIncompleteTransactionsAsync(takeCount)

        Return journals.
            Select(Function(j) New IncompleteTransactionRow With {
                .Id = j.Id,
                .CreatedUtc = j.CreatedUtc,
                .Workflow = j.Workflow,
                .ActionType = j.ActionType,
                .LockerNumber = j.LockerNumber,
                .AssetTag = j.AssetTag,
                .DeviceType = j.DeviceType,
                .TransactionState = j.TransactionState,
                .AckStatus = j.AckStatus,
                .RetryCount = j.RetryCount,
                .LastError = j.LastError
            }).
            ToList()

    End Function
    Public Async Function HasIncompleteTransactionForLockerAsync(lockerNumber As String) As Task(Of Boolean)

        Dim ln As String = If(lockerNumber, "").Trim()
        If ln.Length = 0 Then Return False

        Using db = DatabaseBootstrapper.BuildDbContext()

            Return Await db.LockerTransactionJournals.
                AnyAsync(Function(j) j.LockerNumber = ln AndAlso
                                      j.TransactionState <> LockerTransactionState.Completed AndAlso
                                      j.TransactionState <> LockerTransactionState.Resolved AndAlso
                                      j.TransactionState <> LockerTransactionState.Cancelled)

        End Using

    End Function
    Public Async Function GetJournalDetailAsync(journalId As Integer) As Task(Of String)

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
                AsNoTracking().
                SingleOrDefaultAsync(Function(j) j.Id = journalId)

            If journal Is Nothing Then
                Return $"Journal {journalId} was not found."
            End If

            Return String.Join(
                Environment.NewLine,
                {
                    $"Journal Id: {journal.Id}",
                    $"Created UTC: {journal.CreatedUtc:u}",
                    $"Updated UTC: {journal.UpdatedUtc:u}",
                    $"Completed UTC: {If(journal.CompletedUtc.HasValue, journal.CompletedUtc.Value.ToString("u"), "")}",
                    $"Workflow: {journal.Workflow}",
                    $"Action Type: {journal.ActionType}",
                    $"Locker: {journal.LockerNumber}",
                    $"Branch/Relay: {journal.Branch}/{journal.RelayId}",
                    $"Asset Tag: {journal.AssetTag}",
                    $"Device Type: {journal.DeviceType}",
                    $"Actor: {journal.ActorId}",
                    $"Credential: {journal.Credential}",
                    $"Transaction Id: {journal.TransactionId}",
                    $"Command Id: {journal.CommandId}",
                    $"Correlation Id: {journal.CorrelationId}",
                    $"State: {journal.TransactionState}",
                    $"ACK Status: {journal.AckStatus}",
                    $"Retry Count: {journal.RetryCount}",
                    $"Last Attempt UTC: {If(journal.LastAttemptUtc.HasValue, journal.LastAttemptUtc.Value.ToString("u"), "")}",
                    $"Last Error: {journal.LastError}",
                    "",
                    "Request JSON:",
                    If(String.IsNullOrWhiteSpace(journal.RequestJson), "<empty>", journal.RequestJson),
                    "",
                    "Response JSON:",
                    If(String.IsNullOrWhiteSpace(journal.ResponseJson), "<empty>", journal.ResponseJson)
                })

        End Using

    End Function
    Public Async Function BuildLocalIntegrityReportAsync() As Task(Of String)

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim nowUtc = DateTime.UtcNow
            Dim incomplete = Await GetIncompleteTransactionsAsync(200)

            Dim expiredReservations = Await db.Lockers.
                Include(Function(l) l.Status).
                AsNoTracking().
                Where(Function(l) l.Status IsNot Nothing AndAlso
                                  l.Status.OccupancyState = OccupancyState.Reserved AndAlso
                                  l.Status.ReservedUntilUtc.HasValue AndAlso
                                  l.Status.ReservedUntilUtc.Value <= nowUtc).
                OrderBy(Function(l) l.LockerNumber).
                ToListAsync()

            Dim occupiedMissingAsset = Await db.Lockers.
                Include(Function(l) l.Status).
                AsNoTracking().
                Where(Function(l) l.Status IsNot Nothing AndAlso
                                  l.Status.OccupancyState = OccupancyState.Occupied AndAlso
                                  (String.IsNullOrWhiteSpace(l.Status.CurrentAssetTag) OrElse
                                   String.IsNullOrWhiteSpace(l.Status.CurrentDeviceType))).
                OrderBy(Function(l) l.LockerNumber).
                ToListAsync()

            Dim vacantWithAsset = Await db.Lockers.
                Include(Function(l) l.Status).
                AsNoTracking().
                Where(Function(l) l.Status IsNot Nothing AndAlso
                                  l.Status.OccupancyState = OccupancyState.Vacant AndAlso
                                  (Not String.IsNullOrWhiteSpace(l.Status.CurrentAssetTag) OrElse
                                   Not String.IsNullOrWhiteSpace(l.Status.CurrentDeviceType) OrElse
                                   (l.Status.PackagePresent.HasValue AndAlso l.Status.PackagePresent.Value))).
                OrderBy(Function(l) l.LockerNumber).
                ToListAsync()

            Dim defectiveHolds = Await db.Lockers.
                Include(Function(l) l.Status).
                AsNoTracking().
                Where(Function(l) l.Status IsNot Nothing AndAlso l.Status.IsDefectiveHold).
                OrderBy(Function(l) l.LockerNumber).
                ToListAsync()

            Dim lines As New List(Of String) From {
                $"Local integrity report generated UTC: {DateTime.UtcNow:u}",
                "",
                $"Incomplete transaction journals: {incomplete.Count}",
                $"Expired reservations: {expiredReservations.Count}",
                $"Occupied lockers missing asset/device data: {occupiedMissingAsset.Count}",
                $"Vacant lockers still carrying asset/package data: {vacantWithAsset.Count}",
                $"Defective holds: {defectiveHolds.Count}",
                ""
            }

            AppendJournalSummary(lines, "Incomplete Transactions", incomplete)
            AppendLockerSummary(lines, "Expired Reservations", expiredReservations)
            AppendLockerSummary(lines, "Occupied Missing Asset/Device", occupiedMissingAsset)
            AppendLockerSummary(lines, "Vacant With Asset/Package Data", vacantWithAsset)
            AppendLockerSummary(lines, "Defective Holds", defectiveHolds)

            Return String.Join(Environment.NewLine, lines)

        End Using

    End Function
    Public Async Function RetryAckForJournalAsync(journalId As Integer) As Task(Of Boolean)

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals.
            FirstOrDefaultAsync(Function(j) j.Id = journalId)

            If journal Is Nothing Then
                Return False
            End If

            If journal.AckStatus = LockerAckStatus.Succeeded AndAlso
           journal.TransactionState = LockerTransactionState.Completed Then
                Return True
            End If

            If journal.AckStatus <> LockerAckStatus.Pending AndAlso
           journal.AckStatus <> LockerAckStatus.Failed Then

                journal.TransactionState = LockerTransactionState.NeedsReconciliation
                journal.LastError = "ACK retry requested, but transaction is not in an ACK-retryable state."
                journal.UpdatedUtc = DateTime.UtcNow

                Await db.SaveChangesAsync()
                Return False

            End If

            Dim saveAfterCatch As Boolean = False

            Try
                journal.RetryCount += 1
                journal.LastAttemptUtc = DateTime.UtcNow
                journal.UpdatedUtc = DateTime.UtcNow

                Await db.SaveChangesAsync()

                Dim ackSucceeded As Boolean =
                Await SendJournalAckToBackendAsync(journal)

                journal.UpdatedUtc = DateTime.UtcNow

                If ackSucceeded Then
                    journal.AckStatus = LockerAckStatus.Succeeded
                    journal.TransactionState = LockerTransactionState.Completed
                    journal.CompletedUtc = DateTime.UtcNow
                    journal.LastError = Nothing
                Else
                    journal.AckStatus = LockerAckStatus.Failed
                    journal.TransactionState = LockerTransactionState.AckFailed
                    journal.LastError = "Backend ACK retry failed."

                    If journal.RetryCount >= 5 Then
                        journal.TransactionState = LockerTransactionState.NeedsReconciliation
                        journal.LastError = "Maximum ACK retry count reached."
                    End If
                End If

                Await db.SaveChangesAsync()
                Return ackSucceeded

            Catch ex As Exception

                journal.AckStatus = LockerAckStatus.Failed
                journal.TransactionState = LockerTransactionState.AckFailed
                journal.LastError = ex.Message
                journal.UpdatedUtc = DateTime.UtcNow

                If journal.RetryCount >= 5 Then
                    journal.TransactionState = LockerTransactionState.NeedsReconciliation
                End If

                saveAfterCatch = True

            End Try

            If saveAfterCatch Then
                Await db.SaveChangesAsync()
            End If

            Return False

        End Using

    End Function
    Public Async Function MarkJournalNeedsReconciliationAsync(
        journalId As Integer,
        reason As String
    ) As Task(Of Boolean)

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals _
                .FirstOrDefaultAsync(Function(j) j.Id = journalId)

            If journal Is Nothing Then
                Return False
            End If

            journal.TransactionState = LockerTransactionState.NeedsReconciliation
            journal.LastError = reason
            journal.UpdatedUtc = DateTime.UtcNow

            Await db.SaveChangesAsync()

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.AuditLogFailure,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = "System",
    .AffectedComponent = "LockerTransactionRecoveryService",
    .Outcome = Audit.AuditOutcome.Detected,
    .ReasonCode = $"Locker transaction {journalId} marked NeedsReconciliation. Reason: {reason}"
})

            Return True

        End Using

    End Function
    Public Async Function MarkJournalResolvedAsync(
        journalId As Integer,
        resolvedBy As String,
        resolutionNote As String
    ) As Task(Of Boolean)

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim journal = Await db.LockerTransactionJournals _
                .FirstOrDefaultAsync(Function(j) j.Id = journalId)

            If journal Is Nothing Then
                Return False
            End If

            journal.TransactionState = LockerTransactionState.Resolved
            journal.CompletedUtc = DateTime.UtcNow
            journal.ActorId = resolvedBy
            journal.LastError = resolutionNote
            journal.UpdatedUtc = DateTime.UtcNow

            Await db.SaveChangesAsync()

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.OverrideUsed,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = resolvedBy,
    .AffectedComponent = "LockerTransactionRecoveryService",
    .Outcome = Audit.AuditOutcome.Success,
    .ReasonCode = $"Locker transaction {journalId} manually resolved. Note: {resolutionNote}"
})

            Return True

        End Using

    End Function
    Private Async Function SendJournalAckToBackendAsync(
    journal As LockerTransactionJournal
) As Task(Of Boolean)

        ' TODO:
        ' Wire this to the real backend ACK path.
        ' This method must NEVER reopen the locker.
        ' It should only notify backend transaction completion.

        Await Task.CompletedTask

        Return False

    End Function
    Public Async Function GetIncompleteTransactionsAsync(
        Optional takeCount As Integer = 25
    ) As Task(Of List(Of LockerTransactionJournal))

        Using db = DatabaseBootstrapper.BuildDbContext()

            Return Await db.LockerTransactionJournals _
                .Where(Function(j) j.TransactionState <> LockerTransactionState.Completed AndAlso
                                   j.TransactionState <> LockerTransactionState.Resolved AndAlso
                                   j.TransactionState <> LockerTransactionState.Cancelled) _
                .OrderByDescending(Function(j) j.CreatedUtc) _
                .Take(takeCount) _
                .ToListAsync()

        End Using

    End Function
    Private Shared Sub AppendJournalSummary(
        lines As List(Of String),
        title As String,
        journals As IEnumerable(Of LockerTransactionJournal)
    )

        lines.Add(title & ":")

        Dim any As Boolean = False

        For Each journal In journals.Take(25)
            any = True
            lines.Add(
                $"  Id={journal.Id}; Locker={journal.LockerNumber}; Workflow={journal.Workflow}; " &
                $"Action={journal.ActionType}; State={journal.TransactionState}; Ack={journal.AckStatus}; " &
                $"Error={journal.LastError}")
        Next

        If Not any Then lines.Add("  None")
        lines.Add("")

    End Sub
    Private Shared Sub AppendLockerSummary(
        lines As List(Of String),
        title As String,
        lockers As IEnumerable(Of Locker)
    )

        lines.Add(title & ":")

        Dim any As Boolean = False

        For Each locker In lockers.Take(25)
            any = True
            Dim status = locker.Status
            lines.Add(
                $"  Locker={locker.LockerNumber}; Occ={status?.OccupancyState}; " &
                $"PackagePresent={status?.PackagePresent}; Asset={status?.CurrentAssetTag}; " &
                $"Device={status?.CurrentDeviceType}; LastReason={status?.LastReason}")
        Next

        If Not any Then lines.Add("  None")
        lines.Add("")

    End Sub
End Class
