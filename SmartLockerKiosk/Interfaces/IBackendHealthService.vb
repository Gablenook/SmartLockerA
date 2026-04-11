Imports System.Threading
Imports System.Threading.Tasks

Public Interface IBackendHealthService

    Function CheckHealthAsync(ct As CancellationToken) As Task(Of BackendHealthResult)

End Interface
