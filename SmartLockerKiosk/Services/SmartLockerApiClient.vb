Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json

Public Class SmartLockerApiClient
    Private ReadOnly _http As HttpClient
    Private ReadOnly _baseUrl As String
    Private ReadOnly _kioskId As String
    Private ReadOnly _locationId As String
    Private ReadOnly _apiKey As String

    Private _sessionToken As String

    Private Shared ReadOnly JsonOpts As New JsonSerializerOptions With {
        .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        .PropertyNameCaseInsensitive = True
    }

    Public Sub New(baseUrl As String, kioskId As String, locationId As String, kioskApiKey As String, Optional handler As HttpMessageHandler = Nothing)
        _baseUrl = baseUrl.TrimEnd("/"c)
        _kioskId = kioskId
        _locationId = locationId
        _apiKey = kioskApiKey

        _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
        _http.Timeout = TimeSpan.FromSeconds(15)
        _http.DefaultRequestHeaders.Accept.Clear()
        _http.DefaultRequestHeaders.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
    End Sub

    Public ReadOnly Property SessionToken As String
        Get
            Return _sessionToken
        End Get
    End Property

    Public Sub SetSessionToken(token As String)
        _sessionToken = token
    End Sub

    Private Function NewRequestId() As String
        Return Guid.NewGuid().ToString()
    End Function

    Private Sub ApplyStandardHeaders(req As HttpRequestMessage, requestId As String)
        req.Headers.Remove("X-Request-Id")
        req.Headers.Add("X-Request-Id", requestId)

        req.Headers.Remove("X-Kiosk-Id")
        req.Headers.Add("X-Kiosk-Id", _kioskId)

        If Not String.IsNullOrWhiteSpace(_apiKey) Then
            req.Headers.Remove("X-Api-Key")
            req.Headers.Add("X-Api-Key", _apiKey)
        End If

        If Not String.IsNullOrWhiteSpace(_sessionToken) Then
            req.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _sessionToken)
        End If
    End Sub

    Private Async Function SendAsync(Of TReq, TResp)(
        method As HttpMethod,
        path As String,
        body As TReq,
        includeBearer As Boolean
    ) As Task(Of ApiResult(Of TResp))

        Dim requestId = NewRequestId()

        If Not includeBearer Then
            ' initial authorize call should NOT send Authorization header
            Dim saved = _sessionToken
            _sessionToken = Nothing
            Try
                Return Await SendCoreAsync(Of TReq, TResp)(method, path, body, requestId)
            Finally
                _sessionToken = saved
            End Try
        End If

        Return Await SendCoreAsync(Of TReq, TResp)(method, path, body, requestId)
    End Function

    Private Async Function SendCoreAsync(Of TReq, TResp)(
        method As HttpMethod,
        path As String,
        body As TReq,
        requestId As String
    ) As Task(Of ApiResult(Of TResp))

        Dim url = _baseUrl & path
        Dim json = If(body Is Nothing, Nothing, JsonSerializer.Serialize(body, JsonOpts))

        Using req As New HttpRequestMessage(method, url)
            ApplyStandardHeaders(req, requestId)

            If json IsNot Nothing Then
                req.Content = New StringContent(json, Encoding.UTF8, "application/json")
            End If

            Dim resp As HttpResponseMessage = Nothing
            Try
                resp = Await _http.SendAsync(req)
            Catch ex As TaskCanceledException
                Return New ApiResult(Of TResp) With {
                    .Ok = False,
                    .StatusCode = 0,
                    .Error = New ApiError With {.errorCode = "TIMEOUT", .message = "Request timed out.", .requestId = requestId}
                }
            Catch ex As Exception
                Return New ApiResult(Of TResp) With {
                    .Ok = False,
                    .StatusCode = 0,
                    .Error = New ApiError With {.errorCode = "NETWORK_ERROR", .message = ex.Message, .requestId = requestId}
                }
            End Try

            Dim status = CInt(resp.StatusCode)
            Dim text = Await resp.Content.ReadAsStringAsync()

            If resp.IsSuccessStatusCode Then
                Dim data As TResp = Nothing
                If Not String.IsNullOrWhiteSpace(text) Then
                    data = JsonSerializer.Deserialize(Of TResp)(text, JsonOpts)
                End If
                Return New ApiResult(Of TResp) With {.Ok = True, .StatusCode = status, .Data = data}
            Else
                Dim apiErr As ApiError = Nothing
                Try
                    apiErr = If(String.IsNullOrWhiteSpace(text), Nothing, JsonSerializer.Deserialize(Of ApiError)(text, JsonOpts))
                Catch
                    ' fall through
                End Try

                If apiErr Is Nothing Then
                    apiErr = New ApiError With {.errorCode = "HTTP_" & status.ToString(), .message = text, .requestId = requestId}
                ElseIf String.IsNullOrWhiteSpace(apiErr.requestId) Then
                    apiErr.requestId = requestId
                End If

                Return New ApiResult(Of TResp) With {.Ok = False, .StatusCode = status, .Error = apiErr}
            End If
        End Using
    End Function

    ' -------- Public endpoints --------

    Public Async Function HealthAsync() As Task(Of ApiResult(Of Object))
        ' No bearer needed
        Return Await SendAsync(Of Object, Object)(HttpMethod.Get, "/health", Nothing, includeBearer:=False)
    End Function
    Public Async Function AuthorizeAsync(credential As String, credentialType As String, purpose As String) As Task(Of ApiResult(Of AuthorizeResponse))
        Dim req As New AuthorizeRequest With {
            .credential = credential,
            .credentialType = credentialType,
            .purpose = purpose,
            .kioskId = _kioskId,
            .locationId = _locationId,
            .timestampUtc = DateTime.UtcNow,
            .requestId = Guid.NewGuid().ToString()
        }

        Dim result = Await SendAsync(Of AuthorizeRequest, AuthorizeResponse)(HttpMethod.Post, "/v1/auth/authorize", req, includeBearer:=False)

        If result.Ok AndAlso result.Data IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(result.Data.sessionToken) Then
            _sessionToken = result.Data.sessionToken
        End If

        Return result
    End Function
    Public Async Function AssignLockerAsync(workOrderNumber As String, Optional requestedLockerNumber As String = Nothing) As Task(Of ApiResult(Of AssignLockerResponse))
        Dim req As New AssignLockerRequest With {
            .workOrderNumber = workOrderNumber,
            .kioskId = _kioskId,
            .locationId = _locationId,
            .requestedLockerNumber = requestedLockerNumber,
            .timestampUtc = DateTime.UtcNow,
            .requestId = Guid.NewGuid().ToString()
        }

        Return Await SendAsync(Of AssignLockerRequest, AssignLockerResponse)(HttpMethod.Post, "/v1/workorders/assign-locker", req, includeBearer:=True)
    End Function
End Class
