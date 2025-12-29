Public Class ControllerPort
    ' Primary key: "A" or "B"
    Public Property BranchName As String  ' "A" or "B"
    Public Property PortName As String    ' e.g. "COM7"
    Public Property IsEnabled As Boolean
    Public Property LastVerifiedUtc As DateTime?
End Class

