Namespace SmartLockerKiosk

    Public Module TraceLogger
        Private Const DefaultLogPath As String = "C:\ProgramData\SmartLockerKiosk\Logs\SmartLockerTrace.txt"
        Private ReadOnly RetentionWindow As TimeSpan = TimeSpan.FromHours(24)
        Private ReadOnly PruneInterval As TimeSpan = TimeSpan.FromMinutes(10)
        Private ReadOnly SyncRoot As New Object()
        Private ReadOnly LastPruneByPath As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)

        Public Sub Log(message As String)
            AppendRetainedText(
                DefaultLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}")
        End Sub
        Public Sub AppendRetainedText(logPath As String, entryText As String)
            Try
                If String.IsNullOrWhiteSpace(logPath) Then Return

                Dim dir = IO.Path.GetDirectoryName(logPath)

                If Not String.IsNullOrWhiteSpace(dir) AndAlso Not IO.Directory.Exists(dir) Then
                    IO.Directory.CreateDirectory(dir)
                End If

                SyncLock SyncRoot
                    PruneIfDue(logPath)
                    IO.File.AppendAllText(logPath, If(entryText, ""), Text.Encoding.UTF8)
                End SyncLock
            Catch
            End Try
        End Sub
        Private Sub PruneIfDue(logPath As String)
            Dim nowUtc As DateTime = DateTime.UtcNow
            Dim lastPruneUtc As DateTime = DateTime.MinValue

            If LastPruneByPath.TryGetValue(logPath, lastPruneUtc) AndAlso
               nowUtc - lastPruneUtc < PruneInterval Then
                Return
            End If

            LastPruneByPath(logPath) = nowUtc
            PruneFile(logPath, DateTime.Now.Subtract(RetentionWindow))
        End Sub
        Private Sub PruneFile(logPath As String, cutoffLocal As DateTime)
            If Not IO.File.Exists(logPath) Then Return

            Dim lines = IO.File.ReadAllLines(logPath, Text.Encoding.UTF8)
            If lines.Length = 0 Then Return

            Dim retained As New List(Of String)(lines.Length)
            Dim keepCurrentEntry As Boolean = False

            For Each line In lines
                Dim entryTimestamp As DateTime

                If TryParseEntryTimestamp(line, entryTimestamp) Then
                    keepCurrentEntry = entryTimestamp >= cutoffLocal

                    If keepCurrentEntry Then
                        retained.Add(line)
                    End If
                ElseIf keepCurrentEntry Then
                    retained.Add(line)
                End If
            Next

            IO.File.WriteAllLines(logPath, retained, Text.Encoding.UTF8)
        End Sub
        Private Function TryParseEntryTimestamp(line As String, ByRef timestamp As DateTime) As Boolean
            timestamp = DateTime.MinValue

            If String.IsNullOrWhiteSpace(line) OrElse line.Length < 23 Then
                Return False
            End If

            Dim candidate As String = line.Substring(0, 23)

            Return DateTime.TryParseExact(
                candidate,
                "yyyy-MM-dd HH:mm:ss.fff",
                Globalization.CultureInfo.InvariantCulture,
                Globalization.DateTimeStyles.AssumeLocal,
                timestamp)
        End Function
        Public Sub LogExceptionDeep(label As String, ex As Exception)
            Log("========== " & label & " ==========")

            Dim current = ex
            Dim level = 0

            While current IsNot Nothing
                Log($"[{level}] Type: {current.GetType().FullName}")
                Log($"[{level}] Message: {current.Message}")
                Log($"[{level}] Source: {current.Source}")

                If current.TargetSite IsNot Nothing Then
                    Log($"[{level}] TargetSite: {current.TargetSite.DeclaringType?.FullName}.{current.TargetSite.Name}")
                End If

                Log($"[{level}] StackTrace:")
                Log(current.StackTrace)

                Dim fileEx = TryCast(current, IO.FileNotFoundException)
                If fileEx IsNot Nothing Then
                    Log($"[{level}] FileName: {fileEx.FileName}")
                    Log($"[{level}] FusionLog: {fileEx.FusionLog}")
                End If

                current = current.InnerException
                level += 1
            End While

            Log("========== END " & label & " ==========")
        End Sub
    End Module

End Namespace
