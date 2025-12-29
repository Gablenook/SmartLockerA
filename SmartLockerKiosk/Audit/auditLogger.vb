Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Audit
    Public Class FileAuditLogger
        Implements IAuditLogger
        Private ReadOnly _filePath As String
        Private ReadOnly _sync As New Object()

        Public Sub New(filePath As String)
            _filePath = filePath

            Dim dir = Path.GetDirectoryName(_filePath)
            If Not String.IsNullOrWhiteSpace(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If
        End Sub

        Public Sub Log(ev As AuditEvent) Implements IAuditLogger.Log
            ' Safety: never allow null event
            If ev Is Nothing Then Return

            ' Ensure timestamp is UTC
            If ev.TimestampUtc = DateTimeOffset.MinValue Then
                ev.TimestampUtc = DateTimeOffset.UtcNow
            End If

            ' Serialize as one JSON object per line (easy to parse, append-only)
            Dim json = JsonSerializer.Serialize(ev, New JsonSerializerOptions With {
                .WriteIndented = False
            })

            SyncLock _sync
                File.AppendAllText(_filePath, json & Environment.NewLine, Encoding.UTF8)
            End SyncLock
        End Sub

    End Class

End Namespace

