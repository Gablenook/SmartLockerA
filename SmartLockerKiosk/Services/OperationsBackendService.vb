Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Policy
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows

Namespace SmartLockerKiosk

    Public Class OperationsBackendService
        Implements IOperationsBackendService

        Private ReadOnly _http As HttpClient
        Private ReadOnly _jsonOpts As JsonSerializerOptions

        Private Const AssignLockerPath As String = "v1/workorders/assign-locker"

        Public Sub New(http As HttpClient)
            If http Is Nothing Then Throw New ArgumentNullException(NameOf(http))

            _http = http

            _jsonOpts = New JsonSerializerOptions With {
                .PropertyNameCaseInsensitive = True
            }
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
            Dim backendPurpose = MapPurposeForBackend(AuthPurpose.ValidateIdentity)

            Dim req As New AuthorizeRequest With {
                .credentialkey = value,
                .badgeId = value,
                .kioskId = AppSettings.KioskID,
                .siteCode = AppSettings.SiteCode,
                .clientCode = AppSettings.ClientCode,
                .locationId = AppSettings.LocationId,
                .credentialType = credentialType,
                .purpose = backendPurpose,
                .timestampUtc = DateTime.UtcNow,
                .requestId = requestId
}

            Dim json = JsonSerializer.Serialize(req, _jsonOpts)

            Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.AuthAuthorize, requestId, json)
                Using resp = Await _http.SendAsync(msg, ct)
                    Dim body = Await resp.Content.ReadAsStringAsync()

                    Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                .EventType = Audit.AuditEventType.AuthenticationAttempt,
                .ActorType = Audit.ActorType.System,
                .ActorId = "System:SmartLockerKiosk",
                .AffectedComponent = "OperationsBackendService.AuthValidate",
                .Outcome = If(resp.IsSuccessStatusCode,
                              Audit.AuditOutcome.Success,
                              Audit.AuditOutcome.Error),
                .CorrelationId = requestId,
                .ReasonCode = $"AuthValidateResponse;HTTP={CInt(resp.StatusCode)};Body={body}"
            })

                    If Not resp.IsSuccessStatusCode Then
                        Dim errMsg = ExtractBackendErrorMessage(body, resp)

                        Return New AuthResult With {
                    .IsAuthorized = False,
                    .Purpose = purpose,
                    .Message = errMsg
                }
                    End If

                    Dim dto As AuthorizeResponseDto = Nothing

                    Try
                        dto = JsonSerializer.Deserialize(Of AuthorizeResponseDto)(body, _jsonOpts)

                    Catch ex As JsonException

                        Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
                    .EventType = Audit.AuditEventType.AuthenticationAttempt,
                    .ActorType = Audit.ActorType.System,
                    .ActorId = "System:SmartLockerKiosk",
                    .AffectedComponent = "OperationsBackendService.AuthValidate",
                    .Outcome = Audit.AuditOutcome.Error,
                    .CorrelationId = requestId,
                    .ReasonCode = $"AuthValidateJsonException;HTTP={CInt(resp.StatusCode)};Body={body};Error={ex.Message}"
                })

                        Return New AuthResult With {
                    .IsAuthorized = False,
                    .Purpose = purpose,
                    .Message = "Backend response was not valid auth JSON."
                }

                    End Try

                    If dto Is Nothing Then
                        Return New AuthResult With {
                    .IsAuthorized = False,
                    .Purpose = purpose,
                    .Message = "Backend returned an empty auth response."
                }
                    End If

                    Dim result As New AuthResult With {
                .IsAuthorized = dto.isAuthorized,
                .Purpose = purpose,
                .UserId = dto.userId,
                .DisplayName = dto.displayName,
                .Message = If(dto.isAuthorized, "OK", "Credential not recognized"),
                .SessionToken = dto.sessionToken,
                .WorkOrders = New List(Of WorkOrderAuthItem)()
            }

                    If dto.workOrders IsNot Nothing Then
                        For Each w In dto.workOrders
                            If w Is Nothing Then Continue For

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
        Public Async Function ValidateAssetAsync(
    assetTag As String,
    ct As CancellationToken
) As Task(Of AssetValidateResponse) _
    Implements IOperationsBackendService.ValidateAssetAsync

            Dim requestId = Guid.NewGuid().ToString("N")

            Dim req As New AssetValidateRequest With {
        .assetTag = assetTag,
        .credentialKey = Nothing,
        .kioskId = AppSettings.KioskID,
        .siteCode = AppSettings.SiteCode,
        .locationId = AppSettings.LocationId,
        .clientCode = AppSettings.ClientCode,
        .workflow = "asset-deposit",
        .timestampUtc = DateTime.UtcNow,
        .requestId = requestId
    }

            Dim json = JsonSerializer.Serialize(req, New JsonSerializerOptions With {
        .WriteIndented = True
    })

            Using msg = CreateJsonRequest(HttpMethod.Post, "/asset/validate", requestId, json)
                Using resp = Await _http.SendAsync(msg, ct)

                    Dim body = Await resp.Content.ReadAsStringAsync()

                    MessageBox.Show(
                "URL: " & AppSettings.BaseApiUrl &
                Environment.NewLine &
                "Route: /asset/validate" &
                Environment.NewLine & Environment.NewLine &
                "----- REQUEST -----" &
                Environment.NewLine &
                json &
                Environment.NewLine & Environment.NewLine &
                "HTTP " & CInt(resp.StatusCode).ToString() & " " & resp.ReasonPhrase &
                Environment.NewLine & Environment.NewLine &
                "----- RESPONSE -----" &
                Environment.NewLine &
                body,
                "Asset Validation Debug")

                    If Not resp.IsSuccessStatusCode Then
                        Return New AssetValidateResponse With {
                    .isValid = False,
                    .message = "Backend error"
                }
                    End If

                    Try
                        Return JsonSerializer.Deserialize(Of AssetValidateResponse)(body, _jsonOpts)
                    Catch
                        Return New AssetValidateResponse With {
                    .isValid = False,
                    .message = "Invalid response"
                }
                    End Try

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

            Using msg = CreateJsonRequest(HttpMethod.Post, AssignLockerPath, requestId, json, bearerToken)
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
                        Throw New InvalidOperationException("Commit endpoint not found (404).")
                    End If

                    Throw New InvalidOperationException($"Commit failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")
                End Using
            End Using
        End Function
        Private Function BuildTestAuthResult(credential As String, purpose As AuthPurpose) As AuthResult
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
        Private Function CreateJsonRequest(
    method As HttpMethod,
    relativePath As String,
    requestId As String,
    jsonBody As String,
    Optional bearerToken As String = Nothing
) As HttpRequestMessage

            AppSettings.RequireBackendConfig()

            Dim baseUrl = AppSettings.BaseApiUrl.TrimEnd("/"c)
            Dim path = (If(relativePath, "")).Trim().TrimStart("/"c)
            Dim url = $"{baseUrl}/{path}"

            Dim msg As New HttpRequestMessage(method, New Uri(url, UriKind.Absolute))

            msg.Headers.Add("X-Request-Id", requestId)
            msg.Headers.Add("X-Kiosk-Id", AppSettings.KioskID)
            msg.Headers.Add("X-Api-Key", AppSettings.DeviceApiKey)

            If Not String.IsNullOrWhiteSpace(bearerToken) Then
                msg.Headers.Authorization =
            New AuthenticationHeaderValue("Bearer", bearerToken)
            End If

            msg.Content = New StringContent(jsonBody, Encoding.UTF8, "application/json")

            Return msg

        End Function
        Private Function MapPurposeForBackend(purpose As AuthPurpose) As String
            Select Case purpose
                Case AuthPurpose.ValidateIdentity
                    Return "auth.validate"
                Case AuthPurpose.PickupAccess
                    Return "Pickup"
                Case AuthPurpose.DeliveryCourierAuth
                    Return "Deliver"
                Case AuthPurpose.AdminAccess
                    Return "Admin"
                Case AuthPurpose.DayUseStart
                    Return "DayUse"
                Case Else
                    Return "auth.validate"
            End Select
        End Function
        Private Function MapCredentialTypeForBackend(source As String) As String
            Dim s = (If(source, "")).Trim()

            If s.Length = 0 Then Return "Unknown"

            Select Case s.ToUpperInvariant()
                Case "MANUAL"
                    Return "ManualEntry"
                Case "BARCODE"
                    Return "Barcode"
                Case "QR"
                    Return "QrCode"
                Case "RFID"
                    Return "Rfid"
                Case "NFC"
                    Return "Nfc"
                Case Else
                    Return s
            End Select
        End Function
        Private Function ExtractBackendErrorMessage(body As String, resp As HttpResponseMessage) As String
            If Not String.IsNullOrWhiteSpace(body) Then
                Try
                    Dim apiErr = JsonSerializer.Deserialize(Of ApiErrorDto)(body, _jsonOpts)
                    If apiErr IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(apiErr.message) Then
                        Return apiErr.message
                    End If
                Catch
                End Try

                Return Truncate(body, 300)
            End If

            Return $"Authorization failed ({CInt(resp.StatusCode)})."
        End Function
        Private Function Truncate(value As String, maxLen As Integer) As String
            If String.IsNullOrEmpty(value) Then Return ""
            If value.Length <= maxLen Then Return value
            Return value.Substring(0, maxLen) & "..."
        End Function
        Public Async Function AuthorizeLockerActionAsync(
    dto As LockerAuthorizeRequestDto,
    bearerToken As String,
    ct As CancellationToken
) As Task(Of LockerAuthorizeResponseDto) Implements IOperationsBackendService.AuthorizeLockerActionAsync

            If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))
            If String.IsNullOrWhiteSpace(dto.requestId) Then Throw New ArgumentException("requestId is required.")
            If String.IsNullOrWhiteSpace(dto.correlationId) Then Throw New ArgumentException("correlationId is required.")
            If String.IsNullOrWhiteSpace(dto.requestedBy) Then Throw New ArgumentException("requestedBy is required.")
            If String.IsNullOrWhiteSpace(dto.siteCode) Then Throw New ArgumentException("siteCode is required.")
            If String.IsNullOrWhiteSpace(dto.lockerBankId) Then Throw New ArgumentException("lockerBankId is required.")
            If String.IsNullOrWhiteSpace(dto.lockerId) Then Throw New ArgumentException("lockerId is required.")
            If String.IsNullOrWhiteSpace(dto.doorId) Then Throw New ArgumentException("doorId is required.")
            If String.IsNullOrWhiteSpace(dto.actionType) Then Throw New ArgumentException("actionType is required.")

            If AppSettings.TestModeEnabled Then
                Return BuildTestLockerAuthorizeResponse(dto)
            End If

            AppSettings.RequireBackendConfig()

            Dim json = JsonSerializer.Serialize(dto, _jsonOpts)

            Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.LockerAuthorize, dto.requestId, json, bearerToken)
                Using resp = Await _http.SendAsync(msg, ct)
                    Dim body = Await resp.Content.ReadAsStringAsync()

                    If Not resp.IsSuccessStatusCode Then
                        Dim errMsg = ExtractBackendErrorMessage(body, resp)
                        Throw New InvalidOperationException(errMsg)
                    End If

                    Dim result = JsonSerializer.Deserialize(Of LockerAuthorizeResponseDto)(body, _jsonOpts)
                    If result Is Nothing Then
                        Throw New InvalidOperationException("Locker authorize response was empty.")
                    End If

                    Return result
                End Using
            End Using

        End Function
        Public Async Function AckLockerActionAsync(
            dto As LockerAckRequestDto,
            bearerToken As String,
            ct As CancellationToken
        ) As Task Implements IOperationsBackendService.AckLockerActionAsync

            If dto Is Nothing Then Throw New ArgumentNullException(NameOf(dto))
            If String.IsNullOrWhiteSpace(dto.transactionId) Then Throw New ArgumentException("transactionId is required.")
            If String.IsNullOrWhiteSpace(dto.commandId) Then Throw New ArgumentException("commandId is required.")
            If String.IsNullOrWhiteSpace(dto.correlationId) Then Throw New ArgumentException("correlationId is required.")
            If String.IsNullOrWhiteSpace(dto.ackStatus) Then Throw New ArgumentException("ackStatus is required.")

            If AppSettings.TestModeEnabled Then Return

            AppSettings.RequireBackendConfig()

            If dto.compartmentIds Is Nothing Then
                dto.compartmentIds = New List(Of String)()
            End If

            If String.IsNullOrWhiteSpace(dto.adapterName) Then
                dto.adapterName = AppSettings.AdapterName
            End If

            Dim requestId = Guid.NewGuid().ToString("N")
            Dim json = JsonSerializer.Serialize(dto, _jsonOpts)

            Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.LockerAck, requestId, json, bearerToken)
                Using resp = Await _http.SendAsync(msg, ct)
                    Dim body = Await resp.Content.ReadAsStringAsync()

                    If resp.IsSuccessStatusCode Then Return

                    If resp.StatusCode = HttpStatusCode.Conflict Then Return

                    If resp.StatusCode = HttpStatusCode.Unauthorized OrElse resp.StatusCode = HttpStatusCode.Forbidden Then
                        Throw New InvalidOperationException($"ACK unauthorized ({CInt(resp.StatusCode)}).")
                    End If

                    Throw New InvalidOperationException($"ACK failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")
                End Using
            End Using

        End Function
        Private Function BuildTestLockerAuthorizeResponse(dto As LockerAuthorizeRequestDto) As LockerAuthorizeResponseDto
            Return New LockerAuthorizeResponseDto With {
                .transactionId = "txn_" & Guid.NewGuid().ToString("N"),
                .status = "dispatched",
                .executionMode = "simulator",
                .commandId = "cmd_" & Guid.NewGuid().ToString("N"),
                .auditEventId = "aud_" & Guid.NewGuid().ToString("N"),
                .serverTimeUtc = DateTime.UtcNow.ToString("o"),
                .evidencePointer = $"locker-control/{DateTime.UtcNow:yyyy/MM/dd}/test/{dto.requestId}/authorize-dispatch.json",
                .integrityHashSha256 = Guid.NewGuid().ToString("N"),
                .authorization = New LockerAuthorizeAuthorizationDto With {
                    .isAuthorized = True,
                    .userId = dto.requestedBy,
                    .roles = New List(Of String)()
                }
            }
        End Function
    End Class

End Namespace