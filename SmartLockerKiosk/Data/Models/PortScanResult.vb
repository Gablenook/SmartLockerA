Public Class PortScanResult
    Public Property Branch As String
    Public Property PortName As String
    Public Property DetectedBoards As Integer
    Public Property ScannedRelays As Integer
    Public Property Success As Boolean
    Public Property Message As String
    Public Property Rows As New List(Of RelayScanRow)
End Class