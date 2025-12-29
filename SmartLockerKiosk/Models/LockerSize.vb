Imports System.ComponentModel.DataAnnotations

Public Class LockerSize
    <Key>
    Public Property SizeCode As String          ' e.g. "A", "B", "C", "D", "E", "XL", "S1"

    Public Property DisplayName As String       ' e.g. "Small", "Medium" (optional)

    ' Dimensions (choose your unit; inches shown)
    Public Property WidthIn As Decimal
    Public Property HeightIn As Decimal
    Public Property DepthIn As Decimal

    Public Property SortOrder As Integer = 0
    Public Property IsEnabled As Boolean = True
End Class

