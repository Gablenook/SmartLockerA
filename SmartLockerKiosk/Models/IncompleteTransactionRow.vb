Public Class IncompleteTransactionRow

    Public Property Id As Integer

    Public Property CreatedUtc As DateTime

    Public Property Workflow As String

    Public Property ActionType As String

    Public Property LockerNumber As String

    Public Property AssetTag As String

    Public Property DeviceType As String

    Public Property TransactionState As LockerTransactionState

    Public Property AckStatus As LockerAckStatus

    Public Property RetryCount As Integer

    Public Property LastError As String

End Class
