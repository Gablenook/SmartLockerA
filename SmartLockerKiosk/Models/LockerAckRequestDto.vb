Public Class LockerAckRequestDto
    Public Property transactionId As String
    Public Property commandId As String
    Public Property correlationId As String
    Public Property ackStatus As String
    Public Property adapterName As String
    Public Property hardwareEventCode As String
    Public Property message As String
    Public Property compartmentIds As List(Of String)
End Class
