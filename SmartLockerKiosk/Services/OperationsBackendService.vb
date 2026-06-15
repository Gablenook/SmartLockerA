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
        Public Const AssetValidate As String = "/api/assets/validate"
        Private Const AssignLockerPath As String = "/workorders/assign-locker"

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

            Try
                Dim value As String = If(credential, "").Trim()

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

                Dim requestId As String = Guid.NewGuid().ToString("N")

                Try
                    AppSettings.RequireBackendConfig()

                    Dim credentialType = MapCredentialTypeForBackend(source)
                    Dim backendPurpose = MapPurposeForBackend(AuthPurpose.ValidateIdentity)

                    Dim req As New AuthorizeRequest With {
    .credential = value,
    .kioskId = AppSettings.KioskID,
    .siteCode = AppSettings.SiteCode,
    .clientCode = AppSettings.ClientCode
}

                    Dim json = JsonSerializer.Serialize(req, _jsonOpts)

                    TraceLogger.Log("AUTH REQUEST URL=" & AppSettings.BaseApiUrl & ApiRoutes.AuthAuthorize)
                    TraceLogger.Log("AUTH REQUEST JSON=" & json)

                    Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.AuthAuthorize, requestId, json)
                        Using resp = Await _http.SendAsync(msg, ct)

                            Dim body As String = Await resp.Content.ReadAsStringAsync()

                            TraceLogger.Log("AUTH RESPONSE HTTP=" & CInt(resp.StatusCode).ToString())
                            TraceLogger.Log("AUTH RESPONSE BODY=" & body)


                            Try
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
                            Catch auditEx As Exception
                                TraceLogger.LogExceptionDeep("AUTH_AUDIT_SAFELOG_THROW", auditEx)
                            End Try

                            If Not resp.IsSuccessStatusCode Then
                                Return New AuthResult With {
                            .IsAuthorized = False,
                            .Purpose = purpose,
                            .Message = "Backend authorization service returned an error."
                        }
                            End If

                            Dim dto As AuthorizeResponseDto = Nothing

                            Try
                                dto = JsonSerializer.Deserialize(Of AuthorizeResponseDto)(body, _jsonOpts)
                            Catch jsonEx As JsonException
                                TraceLogger.LogExceptionDeep("AUTH_RESPONSE_JSON_FAIL", jsonEx)

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
                        .ActorID = dto.actorID,
                        .DisplayName = dto.displayName,
                        .Message = If(dto.isAuthorized, "OK", "Credential not recognized"),
                        .SessionToken = dto.sessionToken,
                        .Roles = If(dto.roles, New List(Of String)()),
                        .Permissions = If(dto.Permissions, New List(Of String)()),
                        .AuthorizedDevices = If(dto.authorizedDevices, New List(Of String)()),
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

                Catch ex As OperationCanceledException
                    TraceLogger.LogExceptionDeep("AUTH_AUTHORIZE_CANCELLED", ex)

                    Return New AuthResult With {
                .IsAuthorized = False,
                .Purpose = purpose,
                .Message = "Authorization request was cancelled."
            }

                Catch ex As Exception
                    TraceLogger.LogExceptionDeep("AUTH_AUTHORIZE_FAIL", ex)

                    Return New AuthResult With {
                .IsAuthorized = False,
                .Purpose = purpose,
                .Message = "Credential validation failed."
            }
                End Try
            Catch ex As Exception
                TraceLogger.LogExceptionDeep("AUTHORIZE_CRASH", ex)
                MessageBox.Show("AUTHORIZE CRASH: " & ex.ToString())
                Throw
            End Try


        End Function
        Public Async Function ValidateAssetAsync(
    assetTag As String,
    ct As CancellationToken
) As Task(Of AssetValidateResponse) Implements IOperationsBackendService.ValidateAssetAsync

            Return Await ValidateAssetAsync(
        assetTag:=assetTag,
        credentialKey:=Nothing,
        workflow:="asset-deposit",
        workflowAction:="stage",
        ct:=ct)

        End Function
        Public Async Function ValidateAssetAsync(
    assetTag As String,
    workflow As String,
    workflowAction As String,
    ct As CancellationToken
) As Task(Of AssetValidateResponse)

            Return Await ValidateAssetAsync(
        assetTag:=assetTag,
        credentialKey:=Nothing,
        workflow:=workflow,
        workflowAction:=workflowAction,
        ct:=ct)

        End Function
        Public Async Function ValidateAssetAsync(
    assetTag As String,
    credentialKey As String,
    workflow As String,
    workflowAction As String,
    ct As CancellationToken
) As Task(Of AssetValidateResponse)

            Dim cleanAssetTag As String = If(assetTag, "").Trim()
            Dim cleanCredentialKey As String = If(credentialKey, "").Trim()
            Dim cleanWorkflow As String = If(workflow, "").Trim()
            Dim cleanWorkflowAction As String = If(workflowAction, "").Trim()

            If String.IsNullOrWhiteSpace(cleanAssetTag) Then
                Return New AssetValidateResponse With {
            .isValid = False,
            .message = "Asset tag is required."
        }
            End If

            If String.IsNullOrWhiteSpace(cleanWorkflow) Then
                cleanWorkflow = "asset-deposit"
            End If

            If String.IsNullOrWhiteSpace(cleanWorkflowAction) Then
                cleanWorkflowAction = "stage"
            End If

            If AppSettings.TestModeEnabled Then
                Return New AssetValidateResponse With {
            .isValid = True,
            .assetTag = cleanAssetTag,
            .deviceType = "RF_DEVICE",
            .sizeCode = "",
            .message = "OK"
        }
            End If

            AppSettings.RequireBackendConfig()

            Dim requestId As String = Guid.NewGuid().ToString("N")

            Dim req As New AssetValidateRequest With {
        .assetTag = cleanAssetTag,
        .credentialKey = If(String.IsNullOrWhiteSpace(cleanCredentialKey), Nothing, cleanCredentialKey),
        .kioskId = AppSettings.KioskID,
        .siteCode = AppSettings.SiteCode,
        .locationId = AppSettings.LocationId,
        .clientCode = AppSettings.ClientCode,
        .workflow = cleanWorkflow,
        .workflowAction = cleanWorkflowAction,
        .timestampUtc = DateTime.UtcNow,
        .requestId = requestId
    }

            Dim json As String = JsonSerializer.Serialize(req, _jsonOpts)

            TraceLogger.Log("ASSET VALIDATE URL=" & AppSettings.BaseApiUrl.TrimEnd("/"c) & "/" & AssetValidate.TrimStart("/"c))
            TraceLogger.Log("ASSET VALIDATE REQUEST JSON=" & json)

            Using msg = CreateJsonRequest(HttpMethod.Post, AssetValidate, requestId, json)
                Using resp = Await _http.SendAsync(msg, ct)

                    Dim body As String = Await resp.Content.ReadAsStringAsync()

                    TraceLogger.Log("ASSET VALIDATE RESPONSE HTTP=" & CInt(resp.StatusCode).ToString())
                    TraceLogger.Log("ASSET VALIDATE RESPONSE BODY=" & body)

                    If Not resp.IsSuccessStatusCode Then
                        Return New AssetValidateResponse With {
                    .isValid = False,
                    .message = ExtractBackendErrorMessage(body, resp)
                }
                    End If

                    Dim result As AssetValidateResponse = Nothing

                    Try
                        result = JsonSerializer.Deserialize(Of AssetValidateResponse)(body, _jsonOpts)
                    Catch jsonEx As JsonException
                        TraceLogger.LogExceptionDeep("ASSET_VALIDATE_RESPONSE_JSON_FAIL", jsonEx)

                        Return New AssetValidateResponse With {
                    .isValid = False,
                    .message = "Backend response was not valid asset-validation JSON."
                }
                    End Try

                    If result Is Nothing Then
                        Return New AssetValidateResponse With {
                    .isValid = False,
                    .message = "Backend returned an empty asset-validation response."
                }
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
            If String.IsNullOrWhiteSpace(dto.actorId) Then Throw New ArgumentException("actorId is required.")
            If String.IsNullOrWhiteSpace(dto.siteCode) Then Throw New ArgumentException("siteCode is required.")
            If String.IsNullOrWhiteSpace(dto.lockerBankId) Then Throw New ArgumentException("lockerBankId is required.")
            If String.IsNullOrWhiteSpace(dto.lockerId) Then Throw New ArgumentException("lockerId is required.")
            If String.IsNullOrWhiteSpace(dto.doorId) Then Throw New ArgumentException("doorId is required.")
            If String.IsNullOrWhiteSpace(dto.actionType) Then Throw New ArgumentException("actionType is required.")

            If AppSettings.TestModeEnabled Then
                Return BuildTestLockerAuthorizeResponse(dto)
            End If

            AppSettings.RequireBackendConfig()

            TraceLogger.Log(
        "LOCKER AUTHORIZE DTO: " &
        "requestId=" & If(dto.requestId, "<NULL>") &
        "; correlationId=" & If(dto.correlationId, "<NULL>") &
        "; requestedBy=" & If(dto.requestedBy, "<NULL>") &
        "; actorId=" & If(dto.actorId, "<NULL>") &
        "; lockerId=" & If(dto.lockerId, "<NULL>") &
        "; doorId=" & If(dto.doorId, "<NULL>") &
        "; reasonCode=" & If(dto.reasonCode, "<NULL>"))

            Dim json As String = JsonSerializer.Serialize(dto, _jsonOpts)

            TraceLogger.Log("LOCKER AUTHORIZE URL=" &
                    AppSettings.BaseApiUrl.TrimEnd("/"c) & "/" &
                    ApiRoutes.LockerAuthorize.TrimStart("/"c))

            TraceLogger.Log("LOCKER AUTHORIZE REQUEST JSON=" & json)

            Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.LockerAuthorize, dto.requestId, json, bearerToken)
                Using resp = Await _http.SendAsync(msg, ct)

                    Dim body As String = Await resp.Content.ReadAsStringAsync()

                    TraceLogger.Log("LOCKER AUTHORIZE RESPONSE HTTP=" &
                            CInt(resp.StatusCode).ToString() &
                            " " &
                            resp.ReasonPhrase)

                    TraceLogger.Log("LOCKER AUTHORIZE RESPONSE BODY=" &
                            If(String.IsNullOrWhiteSpace(body), "<empty>", body))

                    If Not resp.IsSuccessStatusCode Then
                        Dim errMsg = ExtractBackendErrorMessage(body, resp)
                        Throw New InvalidOperationException(errMsg)
                    End If

                    Dim result As LockerAuthorizeResponseDto = Nothing

                    Try
                        result = JsonSerializer.Deserialize(Of LockerAuthorizeResponseDto)(body, _jsonOpts)
                    Catch jsonEx As JsonException
                        TraceLogger.LogExceptionDeep("LOCKER_AUTHORIZE_RESPONSE_JSON_FAIL", jsonEx)
                        Throw New InvalidOperationException("Locker authorize response was not valid JSON.", jsonEx)
                    End Try

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

            If AppSettings.TestModeEnabled Then
                TraceLogger.Log("LOCKER ACK SKIPPED - TestModeEnabled=True")
                Return
            End If

            AppSettings.RequireBackendConfig()

            If dto.compartmentIds Is Nothing Then
                dto.compartmentIds = New List(Of String)()
            End If

            If String.IsNullOrWhiteSpace(dto.adapterName) Then
                dto.adapterName = AppSettings.AdapterName
            End If

            Dim requestId = Guid.NewGuid().ToString("N")
            Dim json = JsonSerializer.Serialize(dto, New JsonSerializerOptions With {.WriteIndented = True})

            TraceLogger.Log("LOCKER ACK URL=" & AppSettings.BaseApiUrl.TrimEnd("/"c) & "/" & ApiRoutes.LockerAck.TrimStart("/"c))
            TraceLogger.Log("LOCKER ACK REQUEST JSON:")
            TraceLogger.Log(json)

            Using msg = CreateJsonRequest(HttpMethod.Post, ApiRoutes.LockerAck, requestId, json, bearerToken)
                Using resp = Await _http.SendAsync(msg, ct)

                    Dim body = Await resp.Content.ReadAsStringAsync()

                    TraceLogger.Log($"LOCKER ACK RESPONSE STATUS={CInt(resp.StatusCode)} {resp.ReasonPhrase}")
                    TraceLogger.Log("LOCKER ACK RESPONSE BODY:")
                    TraceLogger.Log(If(String.IsNullOrWhiteSpace(body), "<empty>", body))

                    If Not resp.IsSuccessStatusCode Then
                        If resp.StatusCode = HttpStatusCode.Conflict Then Return

                        If resp.StatusCode = HttpStatusCode.Unauthorized OrElse resp.StatusCode = HttpStatusCode.Forbidden Then
                            Throw New InvalidOperationException($"ACK unauthorized ({CInt(resp.StatusCode)}).")
                        End If

                        Throw New InvalidOperationException($"ACK failed ({CInt(resp.StatusCode)}): {Truncate(body, 300)}")
                    End If

                    If Not String.IsNullOrWhiteSpace(body) Then
                        Using doc = JsonDocument.Parse(body)
                            Dim root = doc.RootElement

                            If root.TryGetProperty("success", Nothing) Then
                                Dim successElement = root.GetProperty("success")

                                If successElement.ValueKind = JsonValueKind.False Then
                                    Dim resultCode As String = ""
                                    Dim message As String = ""

                                    If root.TryGetProperty("resultCode", Nothing) Then
                                        resultCode = root.GetProperty("resultCode").GetString()
                                    End If

                                    If root.TryGetProperty("message", Nothing) Then
                                        message = root.GetProperty("message").GetString()
                                    End If

                                    Throw New InvalidOperationException(
                                $"ACK business failure. resultCode={resultCode}; message={message}")
                                End If
                            End If
                        End Using
                    End If

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