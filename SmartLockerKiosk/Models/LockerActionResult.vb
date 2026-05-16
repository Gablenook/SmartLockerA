Public Class LockerActionResult

    Public Property Success As Boolean

    Public Property JournalId As Integer

    Public Property RequestId As String

    Public Property LockerNumber As String

    Public Property Message As String

    Public Property ErrorMessage As String

    Public Property TransactionState As LockerTransactionState

    Public Property AckStatus As LockerAckStatus

    Public Shared Function Ok(journal As LockerTransactionJournal,
                              Optional message As String = Nothing) As LockerActionResult

        Return New LockerActionResult With {
            .Success = True,
            .JournalId = journal.Id,
            .RequestId = journal.RequestId,
            .LockerNumber = journal.LockerNumber,
            .Message = If(message, "Locker action completed."),
            .TransactionState = journal.TransactionState,
            .AckStatus = journal.AckStatus
        }

    End Function

    Public Shared Function Fail(journal As LockerTransactionJournal,
                                errorMessage As String) As LockerActionResult

        Return New LockerActionResult With {
            .Success = False,
            .JournalId = journal.Id,
            .RequestId = journal.RequestId,
            .LockerNumber = journal.LockerNumber,
            .ErrorMessage = errorMessage,
            .TransactionState = journal.TransactionState,
            .AckStatus = journal.AckStatus
        }

    End Function

End Class