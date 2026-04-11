Option Strict On
Option Explicit On

Imports System.Text
Imports System.Windows.Input

Public Class BarcodeScanService

    Public Event ScanCompleted(scanText As String)
    Public Event ScanRejected(reason As String, rawText As String)
    Public Event Trace(message As String)

    Private ReadOnly _buffer As New StringBuilder()
    Private _lastCharUtc As DateTime = DateTime.MinValue

    Private _lastScan As String = ""
    Private _lastScanUtc As DateTime = DateTime.MinValue

    Public Property ScanTimeoutMs As Integer = 350
    Public Property DuplicateSuppressMs As Integer = 1500
    Public Property MaxLength As Integer = 128
    Public Property IsEnabled As Boolean = True

    '----------------------------------------
    ' INPUT ENTRY POINTS
    '----------------------------------------

    Public Sub HandleTextInput(text As String)
        If Not IsEnabled Then Return
        If String.IsNullOrEmpty(text) Then Return

        For Each ch As Char In text
            HandleChar(ch)
        Next
    End Sub
    Public Sub HandleKeyDown(key As Key)
        If Not IsEnabled Then Return

        If key = Key.Enter OrElse key = Key.Return Then
            FinalizeScan("EnterKey")
        End If
    End Sub

    '----------------------------------------
    ' CORE PROCESSING
    '----------------------------------------

    Private Sub HandleChar(ch As Char)
        Dim now = DateTime.UtcNow

        ' Timeout clears partial scan
        If _lastCharUtc <> DateTime.MinValue AndAlso
           (now - _lastCharUtc).TotalMilliseconds > ScanTimeoutMs Then

            If _buffer.Length > 0 Then
                RaiseEvent Trace($"Buffer timeout. Clearing '{_buffer}'")
            End If

            _buffer.Clear()
        End If

        _lastCharUtc = now

        ' Terminator → finalize
        If ch = ControlChars.Cr OrElse ch = ControlChars.Lf Then
            FinalizeScan("TerminatorChar")
            Return
        End If

        ' Length protection
        If _buffer.Length >= MaxLength Then
            Dim raw = _buffer.ToString()
            _buffer.Clear()

            RaiseEvent ScanRejected("Scan too long", raw)
            Return
        End If

        _buffer.Append(ch)
    End Sub
    Private Sub FinalizeScan(source As String)
        Dim raw = _buffer.ToString()
        _buffer.Clear()

        Dim text = raw.Trim()

        If String.IsNullOrWhiteSpace(text) Then
            RaiseEvent Trace($"Empty scan ignored ({source})")
            Return
        End If

        ' Reject control chars
        If ContainsControlChars(text) Then
            RaiseEvent ScanRejected("Invalid control characters", text)
            Return
        End If

        ' Duplicate suppression
        If IsDuplicate(text) Then
            RaiseEvent Trace($"Duplicate scan suppressed: {text}")
            Return
        End If

        _lastScan = text
        _lastScanUtc = DateTime.UtcNow

        RaiseEvent ScanCompleted(text)
    End Sub

    '----------------------------------------
    ' HELPERS
    '----------------------------------------

    Private Function ContainsControlChars(value As String) As Boolean
        For Each ch In value
            If Char.IsControl(ch) Then Return True
        Next
        Return False
    End Function
    Private Function IsDuplicate(value As String) As Boolean
        If value <> _lastScan Then Return False

        Dim age = (DateTime.UtcNow - _lastScanUtc).TotalMilliseconds
        Return age <= DuplicateSuppressMs
    End Function
    Public Sub Reset()
        _buffer.Clear()
        _lastCharUtc = DateTime.MinValue
    End Sub

End Class
