Imports System.Threading

Public Interface ICommissioningService
    Function BeginCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse))
    Function RecoverCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse))
    Function RegisterHealthAsync(request As RegisterCommissioningHealthRequest, ct As CancellationToken) As Task(Of ApiResult(Of RegisterCommissioningHealthResponse))
    Function FinalizeCommissioningAsync(request As FinalizeCommissioningRequest, ct As CancellationToken) As Task(Of ApiResult(Of FinalizeCommissioningResponse))
End Interface
