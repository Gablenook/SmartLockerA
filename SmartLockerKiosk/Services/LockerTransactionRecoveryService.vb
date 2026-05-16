Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.Audit

Public Class LockerTransactionRecoveryService
    Public Async Function LoadIncompleteTransactionsAsync() As Task(Of List(Of LockerTransactionJournal))

        Using db = DatabaseBootstrapper.BuildDbContext()

            Return Await db.LockerTransactionJournals _
                .Where(Function(j) j.TransactionState <> "Completed" AndAlso
                                   j.TransactionState <> "Resolved") _
                .OrderByDescending(Function(j) j.CreatedUtc) _
                .ToListAsync()

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

            journal.TransactionState = "NeedsReconciliation"
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

            journal.TransactionState = "Resolved"
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
End Class