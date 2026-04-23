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
        'Public Shared Property BaseApiUrl As String = "https://smartlockerapp.azurewebsites.net"
        Public Shared Property BaseApiUrl As String = "http://localhost:5000"



        ' =========================
        ' Workflow selection
        ' =========================

        Public Shared Property WorkflowFamily As String
            Get
                Return GetSetting(
            "SmartLockerKiosk",
            "Workflow",
            "WorkflowFamily",
            "package")
            End Get

            Set(value As String)

                Dim wf = If(value, "").Trim().ToLowerInvariant()

                Select Case wf
                    Case "package", "asset"
                        SaveSetting(
                    "SmartLockerKiosk",
                    "Workflow",
                    "WorkflowFamily",
                    wf)

                    Case Else
                        Throw New ArgumentException(
                    "WorkflowFamily must be 'package' or 'asset'.")
                End Select

            End Set
        End Property

        Public Shared ReadOnly Property HomePickupWorkflowKey As String
            Get

                Select Case WorkflowFamily.Trim().ToLowerInvariant()

                    Case "asset"
                        Return "asset-checkout"

                    Case "package"
                        Return "package-retrieve"

                    Case Else
                        Throw New InvalidOperationException(
                    $"Unknown WorkflowFamily '{WorkflowFamily}'"
                )

                End Select

            End Get
        End Property

        Public Shared ReadOnly Property HomeDeliveryWorkflowKey As String
            Get

                Select Case WorkflowFamily.Trim().ToLowerInvariant()

                    Case "asset"
                        Return "asset-deposit"

                    Case "package"
                        Return "package-deposit"

                    Case Else
                        Throw New InvalidOperationException(
                    $"Unknown WorkflowFamily '{WorkflowFamily}'"
                )

                End Select

            End Get
        End Property


        ' =========================
        ' Scan filter selection
        ' =========================

        Public Shared ReadOnly Property DefaultAssetValidationProfileKey As String
            Get
                Return GetSetting("SmartLockerKiosk", "Workflow", "DefaultAssetValidationProfileKey", "asset_default")
            End Get
        End Property

        ' =========================
        ' Backend communication
        ' =========================

        Public Shared Property ApiTimeoutSeconds As Integer = 10
        Public Shared Property HealthPath As String = "/health"
        Public Shared Property EnableTransactionReporting As Boolean = True
        Public Shared Property EnableQueuedTransactionRetry As Boolean = True
        Public Shared Property TransactionRetryIntervalSeconds As Integer = 30
        Public Shared Property MaxTransactionRetryCount As Integer = 20
        Public Shared Property AdapterName As String = "HldRelayAdapter"

        ' =========================
        ' UI configuration
        ' =========================
        Public Shared Property PasscodeLength As Integer = 6
        Public Shared Property SelectedStyle As String = "TSA-Uniforms"
        Public Shared Property SiteCode As String = "ATL"
        Public Shared Property ClientCode As String = "TSA"
        Public Shared Property LockerBankId As String = ""

        ' =========================
        ' Test / dev mode
        ' =========================
        Public Shared Property UseBackendBypass As Boolean = True
        Public Shared Property TestModeEnabled As Boolean = True

        Public Shared Property TestCommissioningCode As String = "123456"
        Public Shared Property TestAdminCredential As String = "123698740"
        Public Shared Property TestCourierCredential As String = "123698740"
        Public Shared Property TestPickupCredential As String = "123698740"
        Public Shared Property TestWorkOrder As String = "100005"
        Public Shared Property TestPickupLockerNumber As String = "3"
        Public Shared Property TestAssetTag As String = "S/N123456"
        Public Shared Property TestAssetDeviceType As String = "SCANNER"
        Public Shared Property TestDefectType As String = "Battery Issue"


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



