Namespace SmartLockerKiosk

    Public Module TraceLogger
        Public Sub Log(message As String)
            Try
                Dim logPath As String = "C:\Temp\SmartLockerTrace.txt"
                Dim dir = IO.Path.GetDirectoryName(logPath)

                If Not IO.Directory.Exists(dir) Then
                    IO.Directory.CreateDirectory(dir)
                End If

                IO.File.AppendAllText(
                    logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}"
                )
            Catch
            End Try
        End Sub
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