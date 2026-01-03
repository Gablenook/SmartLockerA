Namespace SmartLockerKiosk

    Public NotInheritable Class AppSettings

        Private Sub New()
        End Sub

        ' =========================
        ' Core identity / backend
        ' =========================

        Public Shared Property KioskID As String = ""
        Public Shared Property LocationId As String = ""
        Public Shared Property DeviceApiKey As String = ""
        Public Shared Property BaseApiUrl As String = ""

        ' =========================
        ' UI configuration
        ' =========================

        Public Shared Property PasscodeLength As Integer = 6
        Public Shared Property SelectedWorkFlow As String = "Pickup and Delivery"
        Public Shared Property SelectedStyle As String = "TSA-Uniforms"

        ' =========================
        ' Test / dev mode
        ' =========================

        Public Shared Property TestModeEnabled As Boolean = True
        Public Shared Property TestAdminCredential As String = "123698740"
        Public Shared Property TestCourierCredential As String = "COURIER123"
        Public Shared Property TestPickupCredential As String = "PICKUP123"
        Public Shared Property TestWorkOrder As String = "TSA-1000055"
        Public Shared Property TestPickupLockerNumber As String = "3"

        ' =========================
        ' Derived endpoints (single source of truth)
        ' =========================

        Public Shared ReadOnly Property AuthEndpointUrl As String
            Get
                Return CombineUrl(BaseApiUrl, "/v1/auth/authorize")
            End Get
        End Property

        ' =========================
        ' Guards
        ' =========================

        Public Shared Sub RequireBackendConfig()
            If TestModeEnabled Then Return

            If String.IsNullOrWhiteSpace(BaseApiUrl) Then
                Throw New InvalidOperationException("BaseApiUrl is not configured.")
            End If
            If String.IsNullOrWhiteSpace(KioskID) Then
                Throw New InvalidOperationException("KioskID is not configured.")
            End If
            If String.IsNullOrWhiteSpace(DeviceApiKey) Then
                Throw New InvalidOperationException("DeviceApiKey is not configured.")
            End If
        End Sub

        Public Shared Function HasBackendConfig() As Boolean
            If TestModeEnabled Then Return True

            Return Not String.IsNullOrWhiteSpace(BaseApiUrl) AndAlso
                   Not String.IsNullOrWhiteSpace(KioskID) AndAlso
                   Not String.IsNullOrWhiteSpace(DeviceApiKey)
        End Function

        Private Shared Function CombineUrl(baseUrl As String, relativePath As String) As String
            Dim b = (If(baseUrl, "")).Trim().TrimEnd("/"c)
            Dim r = (If(relativePath, "")).Trim()

            If Not r.StartsWith("/") Then r = "/" & r
            Return b & r
        End Function

    End Class

End Namespace



