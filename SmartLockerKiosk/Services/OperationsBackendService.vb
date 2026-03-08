Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports SmartLockerKiosk.SmartLockerKiosk

Public Class OperationsBackendService
    Implements IOperationsBackendService

    Private Shared ReadOnly _http As New HttpClient()

    Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    Shared Sub New()
        _jsonOpts.Converters.Add(New JsonStringEnumConverter())
    End Sub

    Public Async Function AuthorizeAsync(
        credential As String,
        purpose As AuthPurpose,
        source As String,
        ct As CancellationToken
    ) As Task(Of AuthResult) Implements IOperationsBackendService.AuthorizeAsync

        Dim value = (If(credential, "")).Trim()
        If value.Length = 0 Then
            Return New AuthResult With {
                .IsAuthorized = False,
                .Purpose = purpose,
                .Message = "Empty credential."
            }
        End If

        If AppSettings.TestModeEnabled Then
            Return BuildTestAuthResult(value, purpose)
        End If

        AppSettings.RequireBackendConfig()

        Dim requestId = Guid.NewGuid().ToString("N")
        Dim credentialType = MapCredentialTypeForBackend(source)
        Dim backendPurpose = MapPurposeForBackend(purpose)

        Dim req As New AuthorizeRequest With {
            .credential = value,
            .credentialType = credentialType,
            .purpose = backendPurpose,
            .kioskId = AppSettings.KioskID,
            .locationId = AppSettings.LocationId,
            .timestampUtc = DateTime.UtcNow,
            .requestId = requestId
        }

        Dim json = JsonSerializer.Serialize(req, _jsonOpts)

        Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.AuthAuthorize, requestId, json)
            Using resp = Await _http.SendAsync(msg, ct)
                Dim body = Await resp.Content.ReadAsStringAsync()

                If Not resp.IsSuccessStatusCode Then
                    Dim errMsg = ExtractBackendErrorMessage(body, resp)
                    Return New AuthResult With {
                        .IsAuthorized = False,
                        .Purpose = purpose,
                        .Message = errMsg
                    }
                End If

                Dim dto = JsonSerializer.Deserialize(Of AuthorizeResponseDto)(body, _jsonOpts)

                Dim result As New AuthResult With {
                    .IsAuthorized = (dto IsNot Nothing AndAlso dto.isAuthorized),
                    .Purpose = purpose,
                    .UserId = If(dto IsNot Nothing, dto.userId, Nothing),
                    .DisplayName = If(dto IsNot Nothing, dto.displayName, Nothing),
                    .Message = If(dto IsNot Nothing AndAlso dto.isAuthorized, "OK", "Credential not recognized"),
                    .SessionToken = If(dto IsNot Nothing, dto.sessionToken, Nothing),
                    .WorkOrders = New List(Of WorkOrderAuthItem)()
                }

                If dto IsNot Nothing AndAlso dto.workOrders IsNot Nothing Then
                    For Each w In dto.workOrders
                        result.WorkOrders.Add(New WorkOrderAuthItem With {
                            .WorkOrderNumber = w.workOrderNumber,
                            .TransactionType = w.transactionType,
                            .LockerNumber = w.lockerNumber,
                            .AllowedSizeCode = w.allowedSizeCode
                        })
                    Next
                End If

                Return result
            End Using
        End Using
    End Function

    Public Async Function ReserveLockerAsync(
        workOrderNumber As String,
        requestedLockerNumber As String,
        bearerToken As String,
        ct As CancellationToken
    ) As Task(Of Boolean) Implements IOperationsBackendService.ReserveLockerAsync

        If String.IsNullOrWhiteSpace(bearerToken) Then Return False
        If String.IsNullOrWhiteSpace(workOrderNumber) Then Return False
        If String.IsNullOrWhiteSpace(requestedLockerNumber) Then Return False

        If AppSettings.TestModeEnabled Then
            Return True
        End If

        AppSettings.RequireBackendConfig()

        Dim requestId = Guid.NewGuid().ToString("N")

        Dim req As New AssignLockerRequest With {
            .workOrderNumber = workOrderNumber.Trim(),
            .kioskId = AppSettings.KioskID,
            .locationId = AppSettings.LocationId,
            .requestedLockerNumber = requestedLockerNumber.Trim(),
            .timestampUtc = DateTime.UtcNow,
            .requestId = requestId
        }

        Dim json = JsonSerializer.Serialize(req, _jsonOpts)

        Using msg = CreateJsonRequest(HttpMethod.Post, "v1/workorders/assign-locker", requestId, json, bearerToken)
            Using resp = Await _http.SendAsync(msg, ct)
                If resp.StatusCode = HttpStatusCode.Conflict Then Return False
                Return resp.IsSuccessStatusCode
            End Using
        End Using
    End Function

    Public Async Function CommitDeliveryAsync(
        dto As DeliveryCommitRequestDto,
        bearerToken As String,
        ct As CancellationToken
    ) As Task Implements IOperationsBackendService.CommitDeliveryAsync

        If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))
        If String.IsNullOrWhiteSpace(dto.workOrderNumber) Then Throw New ArgumentException("workOrderNumber is required.")
        If String.IsNullOrWhiteSpace(dto.sizeCode) Then Throw New ArgumentException("sizeCode is required.")
        If String.IsNullOrWhiteSpace(dto.lockerNumber) Then Throw New ArgumentException("lockerNumber is required.")

        If AppSettings.TestModeEnabled Then Return

        AppSettings.RequireBackendConfig()

        Dim requestId = If(String.IsNullOrWhiteSpace(dto.requestId), Guid.NewGuid().ToString("N"), dto.requestId)
        dto.requestId = requestId
        dto.timestampUtc = If(String.IsNullOrWhiteSpace(dto.timestampUtc), DateTime.UtcNow.ToString("o"), dto.timestampUtc)
        dto.kioskId = AppSettings.KioskID
        dto.locationId = AppSettings.LocationId

        Dim json = JsonSerializer.Serialize(dto, _jsonOpts)

        Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.AuthWorkOrder, requestId, json, bearerToken)
            Using resp = Await _http.SendAsync(msg, ct)
                Dim body = Await resp.Content.ReadAsStringAsync()

                If resp.IsSuccessStatusCode Then Return

                If resp.StatusCode = HttpStatusCode.Conflict Then Return

                If resp.StatusCode = HttpStatusCode.Unauthorized OrElse resp.StatusCode = HttpStatusCode.Forbidden Then
                    Throw New InvalidOperationException($"Commit unauthorized ({CInt(resp.StatusCode)}).")
                End If

                If resp.StatusCode = HttpStatusCode.NotFound Then
                    Throw New InvalidOperationException($"Commit endpoint not found (404).")
                End If

                Throw New InvalidOperationException($"Commit failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")
            End Using
        End Using
    End Function

    Private Shared Function BuildTestAuthResult(credential As String, purpose As AuthPurpose) As AuthResult
        Dim result As New AuthResult With {
            .IsAuthorized = False,
            .Purpose = purpose,
            .Message = "Credential not recognized (test mode).",
            .WorkOrders = New List(Of WorkOrderAuthItem)()
        }

        Select Case purpose
            Case AuthPurpose.AdminAccess
                If credential.Equals((If(AppSettings.TestAdminCredential, "")).Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "TEST-ADMIN"
                    result.DisplayName = "Test Admin"
                    result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (test admin)"
                End If

            Case AuthPurpose.DeliveryCourierAuth
                If credential.Equals((If(AppSettings.TestCourierCredential, "")).Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "TEST-COURIER"
                    result.DisplayName = "Test Courier"
                    result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (test courier)"
                End If

            Case AuthPurpose.PickupAccess
                If credential.Equals((If(AppSettings.TestPickupCredential, "")).Trim(), StringComparison.OrdinalIgnoreCase) Then
                    result.IsAuthorized = True
                    result.UserId = "TEST-PICKUP"
                    result.DisplayName = "Test User"
                    result.SessionToken = "TEST-TOKEN-" & Guid.NewGuid().ToString("N")
                    result.Message = "OK (test pickup)"
                    result.WorkOrders.Add(New WorkOrderAuthItem With {
                        .WorkOrderNumber = AppSettings.TestWorkOrder,
                        .TransactionType = "Pickup",
                        .LockerNumber = AppSettings.TestPickupLockerNumber,
                        .AllowedSizeCode = ""
                    })
                End If
        End Select

        Return result
    End Function

    Private Shared Function CreateJsonRequest(
        method As HttpMethod,
        relativePath As String,
        requestId As String,
        jsonBody As String,
        Optional bearerToken As String = Nothing
    ) As HttpRequestMessage

        AppSettings.RequireBackendConfig()

        If _http.BaseAddress Is Nothing Then
            Dim baseUrl = (If(AppSettings.BaseApiUrl, "")).Trim()
            If Not baseUrl.EndsWith("/") Then baseUrl &= "/"
            _http.BaseAddress = New Uri(baseUrl, UriKind.Absolute)
        End If

        Dim path = (If(relativePath, "")).Trim().TrimStart("/"c)

        Dim msg As New HttpRequestMessage(method, New Uri(path, UriKind.Relative))
        msg.Headers.Add("X-Request-Id", requestId)
        msg.Headers.Add("X-Kiosk-Id", AppSettings.KioskID)
        msg.Headers.Add("X-Api-Key", AppSettings.DeviceApiKey)

        If Not String.IsNullOrWhiteSpace(bearerToken) Then
            msg.Headers.Authorization = New AuthenticationHeaderValue("Bearer", bearerToken)
        End If

        msg.Content = New StringContent(jsonBody, Encoding.UTF8, "application/json")
        Return msg
    End Function

    Private Shared Function MapPurposeForBackend(p As AuthPurpose) As String
        Select Case p
            Case AuthPurpose.PickupAccess
                Return "Pickup"
            Case AuthPurpose.DeliveryCourierAuth
                Return "Deliver"
            Case AuthPurpose.AdminAccess
                Return "Admin"
            Case AuthPurpose.DayUseStart
                Return "DayUse"
            Case Else
                Return "Pickup"
        End Select
    End Function

    Private Shared Function MapCredentialTypeForBackend(source As String) As String
        Dim s = (If(source, "")).Trim().ToUpperInvariant()
        If s = "KEYPAD" Then Return "Pin"
        Return "Badge"
    End Function

    Private Shared Function ExtractBackendErrorMessage(body As String, resp As HttpResponseMessage) As String
        Try
            Dim err = JsonSerializer.Deserialize(Of BackendErrorDto)(body, _jsonOpts)
            If err IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(err.message) Then
                Return err.message & $" (HTTP {CInt(resp.StatusCode)})"
            End If
        Catch
        End Try

        Return $"Request failed (HTTP {CInt(resp.StatusCode)})."
    End Function

    Private Shared Function Truncate(s As String, maxLen As Integer) As String
        Dim t = If(s, "")
        If t.Length <= maxLen Then Return t
        Return t.Substring(0, maxLen)
    End Function
End Class