Public Class AuthorizeResponseDto
    Public Property isAuthorized As Boolean
    Public Property userId As String
    Public Property displayName As String
    Public Property sessionToken As String
    Public Property workOrders As List(Of AuthorizeWorkOrderDto)
    Public Property authorizedDevices As List(Of String)
    Public Property Permissions As List(Of String)
    Public Property roles As List(Of String)
    Public Property actorID As String
End Class
