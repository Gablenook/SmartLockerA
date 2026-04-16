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
    Reserved = 5
End Enum

Public Class LockerStatus

    ' =========================
    ' Keys / Relationships
    ' =========================

    <Key, ForeignKey(NameOf(Locker))>
    Public Property LockerId As Integer   ' PK + FK to Locker.LockerId

    Public Overridable Property Locker As Locker

    ' =========================
    ' Core State
    ' =========================

    Public Property LockState As LockState = LockState.Unknown
    Public Property OccupancyState As OccupancyState = OccupancyState.Unknown

    ' Optional signal if you later add a sensor
    Public Property PackagePresent As Boolean?

    ' Last time any status field was updated
    Public Property LastUpdatedUtc As DateTime = DateTime.UtcNow

    ' =========================
    ' Reservation / Assignment Safety
    ' =========================

    ' Prevents double assignment if user taps twice / concurrent flows
    Public Property ReservedUntilUtc As DateTime?
    Public Property ReservedCorrelationId As String
    Public Property ReservedWorkOrderNumber As String

    ' =========================
    ' Traceability / Reconciliation
    ' =========================

    Public Property LastWorkOrderNumber As String
    Public Property LastActorId As String
    Public Property LastReason As String

    ' =========================
    ' Device / Inventory Tracking
    ' =========================

    Public Property CurrentDeviceType As String
    Public Property CurrentAssetTag As String
    Public Property IsDefectiveHold As Boolean = False
    Public Property DefectType As String

End Class

