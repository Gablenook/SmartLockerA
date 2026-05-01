Public Module ApiRoutes

    Public Const AuthAuthorize As String = "/auth/validate"

    '####  Transactional APIs  ####
    Public Const LockerAuthorize As String = "/locker/authorize"
    Public Const LockerAck As String = "/locker/ack"
    Public Const LockerReconcile As String = "/locker/reconcile"
    Public Const AuthWorkOrder As String = ""        'Old test mode endpoint, not used in production. Remove when no longer needed.

    '####  Commissioning APIs  ####
    ' Keep these only if still used by commissioning code.
    Public Const CommissioningBegin As String = "/commissioning/register"
    Public Const CommissioningRecover As String = "/commissioning/recover"
    Public Const CommissioningFinalize As String = "/commissioning/finalize"
    Public Const CommissioningHealthRegister As String = "/commissioning/health/register"
    Public Function CommissioningBootstrap(commissioningSessionId As String) As String
        Return $"v1/commissioning/bootstrap/{commissioningSessionId}"
    End Function
    Public Function KioskBootstrap(kioskId As String) As String
        Return $"v1/kiosks/{kioskId}/bootstrap"
    End Function
End Module