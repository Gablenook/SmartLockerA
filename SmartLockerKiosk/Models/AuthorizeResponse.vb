Public Class AuthorizeResponse
    Public Property isAuthorized As Boolean
    Public Property allowedPurposes As List(Of String)
    Public Property userId As String
    Public Property displayName As String
    Public Property roles As List(Of String)
    Public Property kioskScope As Object          ' keep flexible for now
    Public Property expiresUtc As DateTime
    Public Property sessionToken As String
    Public Property workOrders As List(Of WorkOrderDto)
End Class
