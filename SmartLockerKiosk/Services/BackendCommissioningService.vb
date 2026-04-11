Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

Public Class BackendCommissioningService
    Implements ICommissioningService

    Private ReadOnly _http As HttpClient

    Private Shared ReadOnly _jsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ' Adjust these if the backend developer gives you different commissioning routes
    Private Const BeginPath As String = "api/commissioning/begin"
    Private Const RecoverPath As String = "api/commissioning/recover"
    Private Const RegisterHealthPath As String = "api/commissioning/health"
    Private Const FinalizePath As String = "api/commissioning/finalize"

    Public Sub New(http As HttpClient)
        If http Is Nothing Then Throw New ArgumentNullException(NameOf(http))
        _http = http
    End Sub

    Public Function BeginCommissioningAsync(
    code As String,
    ct As CancellationToken
) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.BeginCommissioningAsync

        Dim request As New BeginCommissioningRequest With {
        .commissioningCode = code,
        .requestSource = "kiosk",
        .deviceInfo = New CommissioningDeviceInfo With {
            .machineName = Environment.MachineName,
            .deviceName = Environment.MachineName,
            .osVersion = Environment.OSVersion.ToString(),
            .appVersion = "",
            .hardwareFingerprint = Nothing
        }
    }

        Return PostJsonAsync(Of BeginCommissioningRequest, BeginCommissioningResponse)(
        BeginPath,
        request,
        ct)
    End Function

    Public Function RecoverCommissioningAsync(
    code As String,
    ct As CancellationToken
) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.RecoverCommissioningAsync

        Dim request As New BeginCommissioningRequest With {
        .commissioningCode = code,
        .requestSource = "kiosk",
        .deviceInfo = New CommissioningDeviceInfo With {
            .machineName = Environment.MachineName,
            .deviceName = Environment.MachineName,
            .osVersion = Environment.OSVersion.ToString(),
            .appVersion = "",
            .hardwareFingerprint = Nothing
        }
    }

        Return PostJsonAsync(Of BeginCommissioningRequest, BeginCommissioningResponse)(
        RecoverPath,
        request,
        ct)
    End Function

    Public Function RegisterHealthAsync(
        request As RegisterCommissioningHealthRequest,
        ct As CancellationToken
    ) As Task(Of ApiResult(Of RegisterCommissioningHealthResponse)) Implements ICommissioningService.RegisterHealthAsync

        Return PostJsonAsync(Of RegisterCommissioningHealthRequest, RegisterCommissioningHealthResponse)(
            RegisterHealthPath,
            request,
            ct)
    End Function

    Public Function FinalizeCommissioningAsync(
        request As FinalizeCommissioningRequest,
        ct As CancellationToken
    ) As Task(Of ApiResult(Of FinalizeCommissioningResponse)) Implements ICommissioningService.FinalizeCommissioningAsync

        Return PostJsonAsync(Of FinalizeCommissioningRequest, FinalizeCommissioningResponse)(
            FinalizePath,
            request,
            ct)
    End Function

    Private Async Function PostJsonAsync(Of TRequest, TResponse)(
        relativeUrl As String,
        payload As TRequest,
        ct As CancellationToken
    ) As Task(Of ApiResult(Of TResponse))

        Dim result As New ApiResult(Of TResponse)

        Try
            Dim json = JsonSerializer.Serialize(payload, _jsonOptions)

            Using request As New HttpRequestMessage(HttpMethod.Post, relativeUrl)
                request.Content = New StringContent(json, Encoding.UTF8, "application/json")

                Using response = Await _http.SendAsync(request, ct)
                    Dim body = Await response.Content.ReadAsStringAsync()

                    result.StatusCode = CInt(response.StatusCode)
                    result.Ok = response.IsSuccessStatusCode

                    If response.IsSuccessStatusCode Then
                        If Not String.IsNullOrWhiteSpace(body) Then
                            result.Data = JsonSerializer.Deserialize(Of TResponse)(body, _jsonOptions)
                        End If
                    Else
                        If Not String.IsNullOrWhiteSpace(body) Then
                            Try
                                result.Error = JsonSerializer.Deserialize(Of ApiError)(body, _jsonOptions)
                            Catch
                                result.Error = New ApiError With {
                                    .errorCode = "HTTP_ERROR",
                                    .message = body
                                }
                            End Try
                        Else
                            result.Error = New ApiError With {
                                .errorCode = "HTTP_ERROR",
                                .message = $"HTTP {result.StatusCode}"
                            }
                        End If
                    End If
                End Using
            End Using

        Catch ex As OperationCanceledException
            result.Ok = False
            result.StatusCode = 0
            result.Error = New ApiError With {
                .errorCode = "REQUEST_CANCELED",
                .message = "The request was canceled or timed out."
            }

        Catch ex As HttpRequestException
            result.Ok = False
            result.StatusCode = 0
            result.Error = New ApiError With {
                .errorCode = "TRANSPORT_ERROR",
                .message = ex.Message
            }

        Catch ex As Exception
            result.Ok = False
            result.StatusCode = 0
            result.Error = New ApiError With {
                .errorCode = "UNEXPECTED_ERROR",
                .message = ex.Message
            }
        End Try

        Return result
    End Function

End Class
