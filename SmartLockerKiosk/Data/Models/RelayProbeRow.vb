Public Class RelayProbeRow
    Public Property Branch As String
    Public Property PortName As String
    Public Property RelayId As Integer
    Public Property LockStatus As Integer?        ' 0=open, 1=closed
    Public Property SensorStatus As Integer?      ' 0=something, 1=nothing
    Public Property Success As Boolean
    Public Property ErrorMessage As String
End Class
