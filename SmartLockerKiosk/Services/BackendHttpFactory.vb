Imports System.Net.Http
Imports System.Net.Http.Headers
Imports SmartLockerKiosk.SmartLockerKiosk

Public NotInheritable Class BackendHttpFactory

    Private Sub New()
    End Sub

    Public Shared Function CreateHttpClient() As HttpClient
        AppSettings.RequireBackendConfig()

        Dim http As New HttpClient()

        Dim baseUrl = AppSettings.BaseApiUrl.Trim()
        If Not baseUrl.EndsWith("/") Then
            baseUrl &= "/"
        End If

        http.BaseAddress = New Uri(baseUrl)
        http.Timeout = TimeSpan.FromSeconds(AppSettings.ApiTimeoutSeconds)

        http.DefaultRequestHeaders.Accept.Clear()
        http.DefaultRequestHeaders.Accept.Add(
            New MediaTypeWithQualityHeaderValue("application/json"))

        http.DefaultRequestHeaders.UserAgent.Clear()
        http.DefaultRequestHeaders.UserAgent.Add(
            New ProductInfoHeaderValue("SmartLockerKiosk", "1.0"))

        If Not String.IsNullOrWhiteSpace(AppSettings.DeviceApiKey) Then
            http.DefaultRequestHeaders.Add("X-Device-ApiKey", AppSettings.DeviceApiKey)
        End If

        If Not String.IsNullOrWhiteSpace(AppSettings.KioskID) Then
            http.DefaultRequestHeaders.Add("X-Kiosk-Id", AppSettings.KioskID)
        End If

        Return http
    End Function

End Class
