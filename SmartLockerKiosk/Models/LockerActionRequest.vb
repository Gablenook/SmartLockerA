Public Class LockerActionRequest

    Public Property Workflow As String
    Public Property ActionType As String

    Public Property LockerId As Integer?
    Public Property LockerNumber As String
    Public Property Branch As String
    Public Property RelayId As Integer?

    Public Property ActorId As String
    Public Property Credential As String
    Public Property AssetTag As String
    Public Property DeviceType As String

    Public Property TransactionId As String
    Public Property CommandId As String
    Public Property CorrelationId As String

    Public Property RequiresBackendAck As Boolean = True

    Public Sub Validate()

        If String.IsNullOrWhiteSpace(Workflow) Then
            Throw New ArgumentException("Workflow is required.")
        End If

        If String.IsNullOrWhiteSpace(ActionType) Then
            Throw New ArgumentException("ActionType is required.")
        End If

        If String.IsNullOrWhiteSpace(LockerNumber) AndAlso Not LockerId.HasValue Then
            Throw New ArgumentException("Either LockerNumber or LockerId is required.")
        End If

    End Sub

End Class
