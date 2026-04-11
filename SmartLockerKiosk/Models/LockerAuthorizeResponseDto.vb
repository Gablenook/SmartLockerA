Public Class LockerAuthorizeResponseDto
    Public Property transactionId As String
    Public Property status As String
    Public Property executionMode As String
    Public Property commandId As String
    Public Property auditEventId As String
    Public Property serverTimeUtc As String
    Public Property evidencePointer As String
    Public Property integrityHashSha256 As String
    Public Property authorization As LockerAuthorizeAuthorizationDto
End Class
