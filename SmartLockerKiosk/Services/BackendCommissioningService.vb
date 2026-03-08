Imports System.Threading

Public Class BackendCommissioningService
    Implements ICommissioningService

    Public Function BeginCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.BeginCommissioningAsync
        Throw New NotImplementedException()
    End Function

    Public Function RecoverCommissioningAsync(code As String, ct As CancellationToken) As Task(Of ApiResult(Of BeginCommissioningResponse)) Implements ICommissioningService.RecoverCommissioningAsync
        Throw New NotImplementedException()
    End Function

    Public Function RegisterHealthAsync(request As RegisterCommissioningHealthRequest, ct As CancellationToken) As Task(Of ApiResult(Of RegisterCommissioningHealthResponse)) Implements ICommissioningService.RegisterHealthAsync
        Throw New NotImplementedException()
    End Function

    Public Function FinalizeCommissioningAsync(request As FinalizeCommissioningRequest, ct As CancellationToken) As Task(Of ApiResult(Of FinalizeCommissioningResponse)) Implements ICommissioningService.FinalizeCommissioningAsync
        Throw New NotImplementedException()
    End Function
End Class
