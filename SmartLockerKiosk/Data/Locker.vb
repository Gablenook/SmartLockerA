Imports System.ComponentModel.DataAnnotations
Imports Microsoft.EntityFrameworkCore

Public Class Locker
    <Key>
    Public Property LockerId As Integer   ' PK (autoincrement in SQLite)
    Public Property Branch As String      ' "A" or "B"
    Public Property RelayId As Integer    ' 1..200
    Public Property LockerNumber As String    ' user-facing label/tag (optional)
    Public Property SizeCode As String
    Public Property Zone As String
    Public Property IsEnabled As Boolean = True
    Public Overridable Property Status As LockerStatus   ' ✅ add this
    Public Overridable Property Size As LockerSize

End Class

