Public Class BeginCommissioningResponse
    Public Property validated As Boolean
    Public Property commissioningSessionId As String
    Public Property commissioningToken As String
    Public Property tenantId As String
    Public Property orgNodeId As String
    Public Property actorId As String
    Public Property kioskId As String
    Public Property kioskName As String
    Public Property branding As CommissioningBrandingDto
    Public Property policy As CommissioningPolicyDto
    Public Property expiresUtc As DateTime?
End Class
