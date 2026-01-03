Public Class PendingDeliveryCommit
    Public Property CommitId As String            ' local GUID
    Public Property RequestId As String           ' correlation/idempotency
    Public Property KioskId As String
    Public Property LocationId As String
    Public Property WorkOrderNumber As String
    Public Property LockerNumber As String
    Public Property SizeCode As String
    Public Property SessionUserId As String       ' optional (courier)
    Public Property CreatedUtc As DateTime
    Public Property AttemptCount As Integer
    Public Property NextAttemptUtc As DateTime
    Public Property LastError As String
End Class

