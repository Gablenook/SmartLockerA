Imports System.Threading

Public Interface IOperationsBackendService
    Function AuthorizeAsync(
        credential As String,
        purpose As AuthPurpose,
        source As String,
        ct As CancellationToken
    ) As Task(Of AuthResult)

    Function ReserveLockerAsync(
        workOrderNumber As String,
        requestedLockerNumber As String,
        bearerToken As String,
        ct As CancellationToken
    ) As Task(Of Boolean)

    Function CommitDeliveryAsync(
        dto As DeliveryCommitRequestDto,
        bearerToken As String,
        ct As CancellationToken
    ) As Task
End Interface