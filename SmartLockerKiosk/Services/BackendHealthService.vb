Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports SmartLockerKiosk.SmartLockerKiosk

Public Class BackendHealthService
    Implements IBackendHealthService

    Private ReadOnly _http As HttpClient

    Private Class HealthResponseDto
        Public Property status As String
        Public Property service As String
        Public Property build As String
    End Class

    Public Sub New(http As HttpClient)
        If http Is Nothing Then Throw New ArgumentNullException(NameOf(http))
        _http = http
    End Sub

    Public Async Function CheckHealthAsync(ct As CancellationToken) As Task(Of BackendHealthResult) _
        Implements IBackendHealthService.CheckHealthAsync

        Dim result As New BackendHealthResult()

        Try
            Dim healthPath = AppSettings.HealthPath
            If String.IsNullOrWhiteSpace(healthPath) Then
                healthPath = "/health"
            End If

            If Not healthPath.StartsWith("/") Then
                healthPath = "/" & healthPath
            End If

            Using response = Await _http.GetAsync(healthPath, ct)

                If Not response.IsSuccessStatusCode Then
                    result.Reachable = False
                    result.DiagnosticMessage = $"Health check failed: HTTP {CInt(response.StatusCode)}."
                    Return result
                End If

                Dim json = Await response.Content.ReadAsStringAsync(ct)

                If String.IsNullOrWhiteSpace(json) Then
                    result.Reachable = True
                    result.DiagnosticMessage = "Health check succeeded, but response body was empty."
                    Return result
                End If

                Dim dto = JsonSerializer.Deserialize(Of HealthResponseDto)(
                    json,
                    New JsonSerializerOptions With {
                        .PropertyNameCaseInsensitive = True
                    })

                result.Reachable = True
                result.Status = If(dto?.status, "")
                result.Service = If(dto?.service, "")
                result.Build = If(dto?.build, "")
                result.DiagnosticMessage = "Backend reachable."

                Return result
            End Using

        Catch ex As TaskCanceledException
            result.Reachable = False
            result.DiagnosticMessage = "Health check timed out."
            Return result

        Catch ex As HttpRequestException
            result.Reachable = False
            result.DiagnosticMessage = "Backend not reachable: " & ex.Message
            Return result

        Catch ex As Exception
            result.Reachable = False
            result.DiagnosticMessage = "Health check failed: " & ex.Message
            Return result
        End Try
    End Function

End Class
