Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Public Enum LockState
    Unknown = 0
    Open = 1
    Closed = 2
End Enum

Public Enum OccupancyState
    Unknown = 0
    Vacant = 1
    Occupied = 2
    Unavailable = 3
    OutOfService = 4
End Enum

Public Class LockerStatus

    <Key, ForeignKey(NameOf(Locker))>
    Public Property LockerId As Integer   ' PK + FK to Locker.LockerId

    Public Overridable Property Locker As Locker

    Public Property LockState As LockState = LockState.Unknown
    Public Property OccupancyState As OccupancyState = OccupancyState.Unknown

    ' Optional signal if you later add a sensor
    Public Property PackagePresent As Boolean?

    ' Last time ANY status field was updated
    Public Property LastUpdatedUtc As DateTime = DateTime.UtcNow

    ' ---------- Delivery-assignment safety ----------
    ' Prevents double assignment if user taps twice / concurrent flows.
    Public Property ReservedUntilUtc As DateTime?
    Public Property ReservedCorrelationId As String
    Public Property ReservedWorkOrderNumber As String

    ' ---------- Traceability (helps support + reconciliation) ----------
    Public Property LastWorkOrderNumber As String
    Public Property LastActorId As String
    Public Property LastReason As String

End Class

