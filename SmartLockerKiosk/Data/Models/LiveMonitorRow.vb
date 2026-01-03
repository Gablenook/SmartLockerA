Public Class LiveMonitorRow
    Public Property RelayId As Integer
    Public Property LockStatus As Integer?
    Public Property SensorStatus As Integer?
    Public Property LastChangeMs As Integer
    Public Property Notes As String

    ' internal tracking (not shown)
    Friend Property _lastLock As Integer?
    Friend Property _lastSensor As Integer?
End Class

