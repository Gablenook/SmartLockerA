Imports System.ComponentModel.DataAnnotations

Public Enum LockerTransactionState
    Created = 0
    DoorOpenRequested = 1
    DoorOpened = 2
    LocalStateUpdated = 3
    AckPending = 4
    AckFailed = 5
    AckSucceeded = 6
    Completed = 7
    NeedsReconciliation = 8
    Resolved = 9
    Cancelled = 10
End Enum

Public Enum LockerAckStatus
    NotRequired = 0
    Pending = 1
    Succeeded = 2
    Failed = 3
End Enum

Public Class LockerTransactionJournal

    <Key>
    Public Property Id As Integer

    <Required>
    <MaxLength(80)>
    Public Property RequestId As String = Guid.NewGuid().ToString("N")

    <MaxLength(80)>
    Public Property TransactionId As String

    <MaxLength(80)>
    Public Property CommandId As String

    <MaxLength(80)>
    Public Property CorrelationId As String

    <Required>
    <MaxLength(80)>
    Public Property KioskId As String

    Public Property LockerId As Integer?

    <MaxLength(30)>
    Public Property LockerNumber As String

    <MaxLength(1)>
    Public Property Branch As String

    Public Property RelayId As Integer?

    <Required>
    <MaxLength(80)>
    Public Property Workflow As String

    <Required>
    <MaxLength(80)>
    Public Property ActionType As String

    <MaxLength(120)>
    Public Property ActorId As String

    <MaxLength(120)>
    Public Property Credential As String

    <MaxLength(120)>
    Public Property AssetTag As String

    <MaxLength(80)>
    Public Property DeviceType As String

    Public Property TransactionState As LockerTransactionState = LockerTransactionState.Created

    Public Property AckStatus As LockerAckStatus = LockerAckStatus.Pending

    Public Property RetryCount As Integer = 0

    Public Property LastAttemptUtc As DateTime?

    Public Property CreatedUtc As DateTime = DateTime.UtcNow

    Public Property UpdatedUtc As DateTime = DateTime.UtcNow

    Public Property CompletedUtc As DateTime?

    Public Property RequestJson As String

    Public Property ResponseJson As String

    Public Property LastError As String

End Class