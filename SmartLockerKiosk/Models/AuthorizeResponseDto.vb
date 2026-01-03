Public Class AuthorizeResponseDto
    Public Property isAuthorized As Boolean
    Public Property userId As String
    Public Property displayName As String
    Public Property sessionToken As String
    Public Property workOrders As List(Of AuthorizeWorkOrderDto)
End Class
