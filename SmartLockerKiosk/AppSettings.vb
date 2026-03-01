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

    End Class

End Namespace



