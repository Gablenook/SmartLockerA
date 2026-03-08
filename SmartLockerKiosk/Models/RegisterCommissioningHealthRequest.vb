Public Class RegisterCommissioningHealthRequest
    Public Property commissioningSessionId As String
    Public Property kioskId As String
    Public Property tenantId As String
    Public Property orgNodeId As String
    Public Property deviceInfo As CommissioningDeviceInfo
    Public Property networkStatus As CommissioningNetworkStatusDto
    Public Property healthStatus As CommissioningHealthStatusDto
    Public Property reportedAtUtc As DateTime
End Class
