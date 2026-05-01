Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports SmartLockerKiosk.SmartLockerKiosk

Public Class BypassOperationsBackendService
    Implements IOperationsBackendService

    Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
    .WriteIndented = False
}

    Public Async Function AuthorizeAsync(
        credential As String,
        purpose As AuthPurpose,
        source As String,
        ct As CancellationToken
    ) As Task(Of AuthResult) Implements IOperationsBackendService.AuthorizeAsync

        Await Task.CompletedTask

        Dim value = (If(credential, "")).Trim()

        Dim result As New AuthResult With {
            .IsAuthorized = False,
            .Purpose = purpose,
            .Message = "Credential not recognized (bypass mode).",
            .WorkOrders = New List(Of WorkOrderAuthItem)()
        }

        Dim input = (If(value, "")).Trim()

        Select Case purpose

            Case AuthPurpose.AdminAccess
                If String.Equals(input, AppSettings.TestAdminCredential.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "BYPASS-ADMIN"
                    result.DisplayName = "Bypass Admin"
                    result.SessionToken = "BYPASS-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (bypass admin)"
                End If

            Case AuthPurpose.DeliveryCourierAuth
                If String.Equals(input, AppSettings.TestCourierCredential.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "BYPASS-COURIER"
                    result.DisplayName = "Bypass Courier"
                    result.SessionToken = "BYPASS-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (bypass courier)"
                End If

            Case AuthPurpose.PickupAccess
                If String.Equals(input, AppSettings.TestPickupCredential.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "BYPASS-PICKUP"
                    result.DisplayName = "Bypass Pickup User"
                    result.SessionToken = "BYPASS-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (bypass pickup)"

                    result.WorkOrders.Add(New WorkOrderAuthItem With {
                .WorkOrderNumber = AppSettings.TestWorkOrder,
                .TransactionType = "Pickup",
                .LockerNumber = AppSettings.TestPickupLockerNumber,
                .AllowedSizeCode = ""
            })
                End If

        End Select

        WriteBypassTrace("authorize", New With {
    .credential = value,
    .purpose = purpose.ToString(),
    .source = source,
    .result = result
})

        Return result
    End Function

    Public Function ValidateAssetAsync(
    assetTag As String,
    ct As CancellationToken
) As Task(Of AssetValidateResponse) _
    Implements IOperationsBackendService.ValidateAssetAsync

        Return Task.FromResult(New AssetValidateResponse With {
        .isValid = True,
        .assetTag = assetTag,
        .deviceType = "SCANNER",
        .sizeCode = "ASSET",
        .status = "Available",
        .message = "OK"
    })
    End Function

    Public Async Function ReserveLockerAsync(
        workOrderNumber As String,
        requestedLockerNumber As String,
        bearerToken As String,
        ct As CancellationToken
    ) As Task(Of Boolean) Implements IOperationsBackendService.ReserveLockerAsync

        Await Task.CompletedTask

        WriteBypassTrace("reserve-locker", New With {
    .workOrderNumber = workOrderNumber,
    .requestedLockerNumber = requestedLockerNumber,
    .bearerTokenPresent = Not String.IsNullOrWhiteSpace(bearerToken)
})

        Return Not String.IsNullOrWhiteSpace(workOrderNumber) AndAlso
               Not String.IsNullOrWhiteSpace(requestedLockerNumber)
    End Function
    Public Async Function CommitDeliveryAsync(
        dto As DeliveryCommitRequestDto,
        bearerToken As String,
        ct As CancellationToken
    ) As Task Implements IOperationsBackendService.CommitDeliveryAsync

        Await Task.CompletedTask

        If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))
        If String.IsNullOrWhiteSpace(dto.workOrderNumber) Then Throw New ArgumentException("workOrderNumber is required.")
        If String.IsNullOrWhiteSpace(dto.lockerNumber) Then Throw New ArgumentException("lockerNumber is required.")
        If String.IsNullOrWhiteSpace(dto.sizeCode) Then Throw New ArgumentException("sizeCode is required.")

        WriteBypassTrace("commit-delivery", dto)
    End Function
    Private Sub WriteBypassTrace(kind As String, payload As Object)
        Try
            Dim dir = IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SmartLockerKiosk",
                "BypassLogs")

            IO.Directory.CreateDirectory(dir)

            Dim path = IO.Path.Combine(dir, "backend-bypass-trace.jsonl")

            Dim lineObj = New With {
                .utc = DateTime.UtcNow.ToString("o"),
                .kind = kind,
                .payload = payload
            }

            Dim line = System.Text.Json.JsonSerializer.Serialize(lineObj, _jsonOpts)
            IO.File.AppendAllText(path, line & Environment.NewLine)
        Catch
        End Try
    End Sub


    Public Async Function AuthorizeLockerActionAsync(
    dto As LockerAuthorizeRequestDto,
    bearerToken As String,
    ct As CancellationToken
) As Task(Of LockerAuthorizeResponseDto) Implements IOperationsBackendService.AuthorizeLockerActionAsync

        Await Task.CompletedTask

        If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))

        WriteBypassTrace("locker-authorize", dto)

        Return New LockerAuthorizeResponseDto With {
            .transactionId = "bypass-txn-" & Guid.NewGuid().ToString("N"),
            .status = "dispatched",
            .executionMode = "simulator",
            .commandId = "bypass-cmd-" & Guid.NewGuid().ToString("N"),
            .auditEventId = "bypass-aud-" & Guid.NewGuid().ToString("N"),
            .serverTimeUtc = DateTime.UtcNow.ToString("o"),
            .evidencePointer = "bypass/locker-authorize",
            .integrityHashSha256 = Guid.NewGuid().ToString("N"),
            .authorization = New LockerAuthorizeAuthorizationDto With {
                .isAuthorized = True,
                .userId = dto.requestedBy,
                .roles = New List(Of String)()
            }
        }
    End Function

    Public Async Function AckLockerActionAsync(
        dto As LockerAckRequestDto,
        bearerToken As String,
        ct As CancellationToken
    ) As Task Implements IOperationsBackendService.AckLockerActionAsync

        Await Task.CompletedTask

        If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))

        WriteBypassTrace("locker-ack", dto)
    End Function
End Class
