Imports System.IO.Ports
Imports System.Threading.Tasks

Public Class HldPortScanner

    Private Const ProbeTimeoutMs As Integer = 1500
    Private Const MinScoreToInclude As Integer = 1

    Public Async Function ScanCandidatesAsync() As Task(Of List(Of PortCandidate))
        Dim results As New List(Of PortCandidate)

        Dim ports = SerialPort.GetPortNames().OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase).ToList()

        For Each port In ports
            Dim score = Await ProbePortScoreAsync(port).ConfigureAwait(False)
            If score >= MinScoreToInclude Then
                results.Add(New PortCandidate With {.PortName = port, .Score = score})
            End If
        Next

        Return results.OrderByDescending(Function(c) c.Score).ToList()
    End Function

    Private Async Function ProbePortScoreAsync(portName As String) As Task(Of Integer)
        Dim ctl As HldRelayController.HldRelayController = Nothing

        Try
            ctl = New HldRelayController.HldRelayController(autoReconnect:=False)

            Dim tcs As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

            AddHandler ctl.StatusUpdated,
                Sub()
                    If Not tcs.Task.IsCompleted Then tcs.TrySetResult(True)
                End Sub

            ctl.Start(portName)

            Dim gotFrame As Boolean = Await WaitWithTimeoutAsync(tcs.Task, TimeSpan.FromMilliseconds(ProbeTimeoutMs)).ConfigureAwait(False)
            If Not gotFrame OrElse ctl.LastFrameUtc = DateTime.MinValue Then Return 0

            Return If(ctl.IsCommsHealthy, 95, 70)

        Catch
            Return 0
        Finally
            If ctl IsNot Nothing Then
                Try : ctl.Dispose() : Catch : End Try
            End If
        End Try
    End Function

    Private Shared Async Function WaitWithTimeoutAsync(task As Task, timeout As TimeSpan) As Task(Of Boolean)
        Dim delay = Task.Delay(timeout)
        Dim completed = Await Task.WhenAny(task, delay).ConfigureAwait(False)
        Return completed Is task
    End Function

End Class

