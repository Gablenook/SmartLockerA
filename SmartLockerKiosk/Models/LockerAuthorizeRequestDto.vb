Public Class LockerAuthorizeRequestDto
    Public Property requestId As String
    Public Property correlationId As String
    Public Property requestedBy As String
    Public Property requestedByType As String
    Public Property siteCode As String
    Public Property lockerBankId As String
    Public Property lockerId As String
    Public Property doorId As String
    Public Property actionType As String
    Public Property requestedAtUtc As String
    Public Property reasonCode As String
    Public Property metadata As LockerAuthorizeMetadataDto
End Class
