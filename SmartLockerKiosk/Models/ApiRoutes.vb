Public Module ApiRoutes
    Public Const AuthAuthorize As String = "v1/auth/authorize"
    Public Const AuthWorkOrder As String = "v1/workorders/commit-assignment"

    Public Const CommissioningBegin As String = "v1/commissioning/begin"
    Public Const CommissioningRecover As String = "v1/commissioning/recover"
    Public Const CommissioningFinalize As String = "v1/commissioning/finalize"
    Public Const CommissioningHealthRegister As String = "v1/commissioning/health/register"

    Public Function CommissioningBootstrap(commissioningSessionId As String) As String
        Return $"v1/commissioning/bootstrap/{commissioningSessionId}"
    End Function

    Public Function KioskBootstrap(kioskId As String) As String
        Return $"v1/kiosks/{kioskId}/bootstrap"
    End Function
End Module
