Public Class ApiResult(Of T)
    Public Property Ok As Boolean
    Public Property StatusCode As Integer
    Public Property Data As T
    Public Property [Error] As ApiError
End Class