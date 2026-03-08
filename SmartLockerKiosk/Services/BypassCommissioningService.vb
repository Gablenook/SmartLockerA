Imports System.Threading
Imports System.Threading.Tasks

Public Class BypassCommissioningService
    Implements ICommissioningService

    Private ReadOnly _expectedCode As String

    Public Sub New(expectedCode As String)
        _expectedCode = expectedCode
    End Sub

    Public Async Function BeginCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.BeginCommissioningAsync
        Await Task.CompletedTask

        If Not String.Equals(code, _expectedCode, StringComparison.Ordinal) Then
            Return New ApiResult(Of BeginCommissioningResponse) With {
                .Ok = False,
                .StatusCode = 401,
                .Error = New ApiError With {
                    .errorCode = "INVALID_COMMISSIONING_CODE",
                    .message = "Invalid Commissioning ID.",
                    .requestId = Guid.NewGuid().ToString()
                }
            }
        End If

        Return New ApiResult(Of BeginCommissioningResponse) With {
            .Ok = True,
            .StatusCode = 200,
            .Data = New BeginCommissioningResponse With {
                .validated = True,
                .commissioningSessionId = Guid.NewGuid().ToString(),
                .commissioningToken = "BYPASS-COMMISSIONING-TOKEN",
                .tenantId = Guid.NewGuid().ToString(),
                .orgNodeId = Guid.NewGuid().ToString(),
                .actorId = Guid.NewGuid().ToString(),
                .kioskId = Guid.NewGuid().ToString(),
                .kioskName = Environment.MachineName,
                .branding = New CommissioningBrandingDTO With {
                    .organizationName = "Local Test Organization",
                    .logoUri = "",
                    .secondaryLogoUri = "",
                    .accentColor = "#003366"
                },
                .policy = New CommissioningPolicyDTO With {
                    .requireControllerSetup = True,
                    .requireLockerCommissioning = True,
                    .requireHealthRegistration = True
                },
                .expiresUtc = DateTime.UtcNow.AddMinutes(15)
            }
        }
    End Function

    Public Async Function RecoverCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.RecoverCommissioningAsync
        Return Await BeginCommissioningAsync(code, ct)
    End Function

    Public Async Function RegisterHealthAsync(request As RegisterCommissioningHealthRequest, ct As CancellationToken) As Task(Of ApiResult(Of RegisterCommissioningHealthResponse)) Implements ICommissioningService.RegisterHealthAsync
        Await Task.CompletedTask

        Return New ApiResult(Of RegisterCommissioningHealthResponse) With {
            .Ok = True,
            .StatusCode = 200,
            .Data = New RegisterCommissioningHealthResponse With {
                .registered = True,
                .healthRegistrationId = Guid.NewGuid().ToString(),
                .registeredAtUtc = DateTime.UtcNow
            }
        }
    End Function

    Public Async Function FinalizeCommissioningAsync(request As FinalizeCommissioningRequest, ct As CancellationToken) As Task(Of ApiResult(Of FinalizeCommissioningResponse)) Implements ICommissioningService.FinalizeCommissioningAsync
        Await Task.CompletedTask

        Return New ApiResult(Of FinalizeCommissioningResponse) With {
            .Ok = True,
            .StatusCode = 200,
            .Data = New FinalizeCommissioningResponse With {
                .commissioned = True,
                .commissionedAtUtc = DateTime.UtcNow,
                .kioskToken = "BYPASS-KIOSK-TOKEN",
                .refreshToken = Nothing,
                .configuration = New CommissioningRuntimeConfigDTO With {
                    .heartbeatIntervalSeconds = 60,
                    .commandAckTimeoutSeconds = 15,
                    .brandingCacheTtlSeconds = 86400
                }
            }
        }
    End Function
End Class