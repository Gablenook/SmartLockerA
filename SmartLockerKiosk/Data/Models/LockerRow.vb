Namespace SmartLockerKiosk
    Public Class LockerRow
        Public Property LockerId As Integer      ' 0 = new
        Public Property Branch As String          ' "A" / "B"
        Public Property RelayId As Integer        ' 1..200
        Public Property LockerNumber As String
        Public Property SizeCode As String
        Public Property Zone As String
        Public Property IsEnabled As Boolean
    End Class

End Namespace