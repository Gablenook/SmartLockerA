Public Class WorkOrderAuthItem
    Public Property WorkOrderNumber As String
    Public Property TransactionType As String  ' "Pickup" / "Delivery" / etc.
    Public Property LockerNumber As String     ' null/empty for Delivery when not assigned
    Public Property AllowedSizeCode As String  ' optional
End Class