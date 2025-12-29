Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Public Enum LockState
    Unknown = 0
    Open = 1
    Closed = 2
End Enum

Public Enum OccupancyState
    Unknown = 0
    Available = 1
    Occupied = 2
    OutOfService = 3
End Enum

Public Class LockerStatus
    <Key, ForeignKey(NameOf(Locker))>
    Public Property LockerId As Integer   ' PK + FK to Locker.LockerId
    Public Overridable Property Locker As Locker
    Public Property LockState As LockState = LockState.Unknown
    Public Property OccupancyState As OccupancyState = OccupancyState.Unknown
    Public Property PackagePresent As Boolean?
    Public Property LastUpdatedUtc As DateTime = DateTime.UtcNow
End Class

