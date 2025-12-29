Public Class PortCandidate
    Public Property PortName As String
    Public Property Score As Integer

    Public Overrides Function ToString() As String
        Return $"{PortName} (score {Score})"
    End Function
End Class

