Imports System.IO.Ports
Imports HldSerialLib.Serial.LockBoard

Public Class HldPortScanner

    Private ReadOnly _probeRelays As Integer() = {1, 2, 3}

    Public Function ScanCandidates() As List(Of PortCandidate)
        Dim results As New List(Of PortCandidate)

        For Each port In SerialPort.GetPortNames().OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase)
            Dim score As Integer = ProbePortScore(port)
            If score >= 60 Then
                results.Add(New PortCandidate With {.PortName = port, .Score = score})
            End If
        Next

        Return results.OrderByDescending(Function(c) c.Score).ToList()
    End Function

    Private Function ProbePortScore(portName As String) As Integer
        Dim board As HldLockBoard = Nothing

        Try
            board = New HldLockBoard()
            board.Open(portName, 115200)
            Threading.Thread.Sleep(150)

            Dim successes As Integer = 0

            For Each relayId In _probeRelays
                Try
                    board.GetLockStatus(relayId)
                    successes += 1
                Catch
                    Exit For
                End Try
            Next

            If successes = 0 Then Return 0
            If successes = 1 Then Return 70
            If successes = 2 Then Return 85
            Return 95

        Catch
            Return 0

        Finally
            If board IsNot Nothing Then
                Try : board.Close() : Catch : End Try
            End If
        End Try
    End Function

End Class

