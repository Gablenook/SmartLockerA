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
        Public Shared Property BaseApiUrl As String = "https://smartlockerapp.azurewebsites.net"

        ' =========================
        ' Backend communication
        ' =========================

        Public Shared Property ApiTimeoutSeconds As Integer = 10
        Public Shared Property HealthPath As String = "/health"

        ' Optional: post-action reporting behavior
        Public Shared Property EnableTransactionReporting As Boolean = True
        Public Shared Property EnableQueuedTransactionRetry As Boolean = True
        Public Shared Property TransactionRetryIntervalSeconds As Integer = 30
        Public Shared Property MaxTransactionRetryCount As Integer = 20

        ' Adapter identity for backend reporting
        Public Shared Property AdapterName As String = "HldRelayAdapter"

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
        Public Shared Property TestCourierCredential As String = "123698740"
        Public Shared Property TestPickupCredential As String = "123698740"
        Public Shared Property TestWorkOrder As String = "1000055"
        Public Shared Property TestPickupLockerNumber As String = "3"

        ' When True, kiosk may use bypass services instead of real backend services
        Public Shared Property UseBackendBypass As Boolean = True

        ' =========================
        ' Guards
        ' =========================

        Public Shared Sub RequireBackendConfig()
            If UseBackendBypass Then Return

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
            If UseBackendBypass Then Return True

            Return Not String.IsNullOrWhiteSpace(BaseApiUrl) AndAlso
               Not String.IsNullOrWhiteSpace(KioskID) AndAlso
               Not String.IsNullOrWhiteSpace(DeviceApiKey)
        End Function

    End Class

End Namespace



