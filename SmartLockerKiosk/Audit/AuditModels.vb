Namespace Audit
    Public Enum AuditEventType
        ' Identity / Authentication
        AuthenticationAttempt
        AuthenticationTimeout
        AuthenticationLocked
        MFAChallengeIssued
        MFAChallengeFailed

        ' Locker / Custody
        LockerOpenAttempt
        LockerDoorOpen
        LockerDoorDenied
        AssetDeposited
        AssetRetrieved
        CustodyTransfer
        OverrideUsed

        ' Administration / Configuration
        AdminLogin
        AdminLogout
        RoleAssignmentChange
        PolicyConfigurationChange
        LockerEnabledDisabled
        FirmwareUpdateApplied

        ' System / Security
        SystemStartup
        SystemShutdown
        HardwareFaultDetected
        CommunicationFailure
        IntegrityCheckFailed
        MalwareScanEvent

        ' Incident / Monitoring signals
        ExcessiveAuthFailures
        TamperDetected
        AuditLogFailure
        UnauthorizedPeripheral

        'Hardware Events
        ControllerConnectionTest

    End Enum
    Public Enum AuditOutcome
        Success
        Failure
        Denied
        Timeout
        [Error]
        Cancelled
        Detected
    End Enum
    Public Enum ActorType
        User
        Admin
        System
        Device
        Service
    End Enum
    Public Class AuditEvent
        Public Property TimestampUtc As DateTimeOffset
        Public Property EventType As AuditEventType
        Public Property ActorType As ActorType
        Public Property ActorId As String
        Public Property AffectedComponent As String
        Public Property Outcome As AuditOutcome
        Public Property CorrelationId As String
        Public Property ReasonCode As String
        Public Sub New()
            TimestampUtc = DateTimeOffset.UtcNow
        End Sub
    End Class

End Namespace

