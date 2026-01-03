Public Class AuthProfile
    Public Property IsAuthenticated As Boolean
    Public Property IsAdmin As Boolean
    Public Property CanPickup As Boolean
    Public Property CanDeliver As Boolean
    Public Property SessionToken As String
    Public Property WorkOrders As List(Of WorkOrderAuthItem) =
        New List(Of WorkOrderAuthItem)()
End Class

