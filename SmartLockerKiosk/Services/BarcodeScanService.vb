Imports System.Text
Imports System.Windows.Input

Public Class BarcodeScanService

    Public Event ScanCompleted(scanText As String)
    Public Event ScanRejected(reason As String, rawText As String)
    Public Event Trace(message As String)

    Private ReadOnly _buffer As New StringBuilder()
    Private _lastCharUtc As DateTime = DateTime.MinValue

    Private _lastScan As String = String.Empty
    Private _lastScanUtc As DateTime = DateTime.MinValue

    Private _ignoreLeadingLf As Boolean = False

    Public Property ScanTimeoutMs As Integer = 350
    Public Property DuplicateSuppressMs As Integer = 1500
    Public Property MinLength As Integer = 1
    Public Property MaxLength As Integer = 128
    Public Property IsEnabled As Boolean = True

    Private WithEvents _idleTimer As New System.Windows.Threading.DispatcherTimer With {
    .Interval = TimeSpan.FromMilliseconds(350)
}

    ''' <summary>
    ''' Optional custom validator.
    ''' When Nothing, the service performs only structural validation
    ''' and accepts all non-empty scans within length limits.
    ''' </summary>
    Public Property Validator As Func(Of String, ScanValidationResult)


    ' INPUT ENTRY POINTS
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

    ' CORE PROCESSING

    Private Sub HandleChar(ch As Char)
        Dim nowUtc As DateTime = DateTime.UtcNow

        ' Ignore LF immediately following a CR terminator.
        If _ignoreLeadingLf AndAlso ch = ControlChars.Lf Then
            _ignoreLeadingLf = False
            RaiseEvent Trace("Ignored LF following CR terminator.")
            Return
        End If

        _ignoreLeadingLf = False

        ' If a previous scan is sitting in the buffer and no terminator arrived,
        ' treat the idle gap as the end of that scan.
        If _lastCharUtc <> DateTime.MinValue AndAlso
       (nowUtc - _lastCharUtc).TotalMilliseconds > ScanTimeoutMs Then

            If _buffer.Length > 0 Then
                RaiseEvent Trace($"Buffer timeout after {(nowUtc - _lastCharUtc).TotalMilliseconds:F0} ms. Finalizing partial scan '{_buffer}'.")
                FinalizeScan("Timeout")
            End If
        End If

        _lastCharUtc = nowUtc

        ' Terminators finalize the current scan.
        If ch = ControlChars.Cr Then
            FinalizeScan("CR")
            _ignoreLeadingLf = True
            Return
        End If

        If ch = ControlChars.Lf Then
            FinalizeScan("LF")
            Return
        End If

        ' Reject embedded control characters.
        If Char.IsControl(ch) Then
            Dim raw As String = _buffer.ToString()
            _buffer.Clear()

            RaiseEvent ScanRejected("Invalid control character received during scan.", raw)
            RaiseEvent Trace("Rejected scan due to embedded control character.")
            Return
        End If

        ' Protect against runaway input.
        If _buffer.Length >= MaxLength Then
            Dim raw As String = _buffer.ToString()
            _buffer.Clear()

            RaiseEvent ScanRejected($"Scan too long (>{MaxLength} characters).", raw)
            RaiseEvent Trace($"Rejected scan because max length {MaxLength} was exceeded.")
            Return
        End If

        _buffer.Append(ch)

        RestartIdleTimer()
    End Sub
    Private Sub FinalizeScan(source As String)
        _idleTimer.Stop()
        Dim raw As String = _buffer.ToString()
        _buffer.Clear()

        Dim text As String = raw.Trim()

        If String.IsNullOrWhiteSpace(text) Then
            RaiseEvent Trace($"Empty scan ignored ({source}).")
            Return
        End If

        Dim validation As ScanValidationResult = ValidateScan(text)

        If validation Is Nothing Then
            validation = ScanValidationResult.Invalid("Validation returned no result.")
        End If

        If Not validation.IsValid Then
            RaiseEvent ScanRejected(validation.Reason, text)
            RaiseEvent Trace($"Rejected scan ({source}): '{text}'. Reason: {validation.Reason}")
            Return
        End If

        If IsDuplicate(text) Then
            RaiseEvent Trace($"Duplicate scan suppressed: '{text}'.")
            Return
        End If

        _lastScan = text
        _lastScanUtc = DateTime.UtcNow

        RaiseEvent Trace($"Accepted scan ({source}): '{text}'.")
        RaiseEvent ScanCompleted(text)
    End Sub

    ' VALIDATION
    Private Function ValidateScan(text As String) As ScanValidationResult
        If text Is Nothing Then
            Return ScanValidationResult.Invalid("Scan was null.")
        End If

        If text.Length < MinLength Then
            Return ScanValidationResult.Invalid($"Scan too short (<{MinLength} characters).")
        End If

        If text.Length > MaxLength Then
            Return ScanValidationResult.Invalid($"Scan too long (>{MaxLength} characters).")
        End If

        If ContainsControlChars(text) Then
            Return ScanValidationResult.Invalid("Invalid control characters detected.")
        End If

        ' No validator means no content filtering.
        If Validator Is Nothing Then
            Return ScanValidationResult.Valid()
        End If

        Dim customResult As ScanValidationResult = Validator.Invoke(text)

        If customResult Is Nothing Then
            Return ScanValidationResult.Invalid("Validator returned no result.")
        End If

        Return customResult
    End Function

    ' HELPERS
    Private Function ContainsControlChars(value As String) As Boolean
        For Each ch As Char In value
            If Char.IsControl(ch) Then Return True
        Next

        Return False
    End Function
    Private Function IsDuplicate(value As String) As Boolean
        If Not String.Equals(value, _lastScan, StringComparison.Ordinal) Then
            Return False
        End If

        Dim ageMs As Double = (DateTime.UtcNow - _lastScanUtc).TotalMilliseconds
        Return ageMs <= DuplicateSuppressMs
    End Function
    Public Sub ResetBuffer()
        _idleTimer.Stop()
        _buffer.Clear()
        _lastCharUtc = DateTime.MinValue
        _ignoreLeadingLf = False

        RaiseEvent Trace("Barcode input buffer reset.")
    End Sub
    Public Sub ResetAll()
        _idleTimer.Stop()
        _buffer.Clear()
        _lastCharUtc = DateTime.MinValue
        _lastScan = String.Empty
        _lastScanUtc = DateTime.MinValue
        _ignoreLeadingLf = False

        RaiseEvent Trace("Barcode scanner state fully reset.")
    End Sub
    Private Sub RestartIdleTimer()
        _idleTimer.Stop()
        _idleTimer.Interval = TimeSpan.FromMilliseconds(ScanTimeoutMs)
        _idleTimer.Start()
    End Sub
    Private Sub IdleTimer_Tick(sender As Object, e As EventArgs) Handles _idleTimer.Tick
        _idleTimer.Stop()

        If Not IsEnabled Then Return

        If _buffer.Length > 0 Then
            RaiseEvent Trace($"Idle timeout after {ScanTimeoutMs} ms. Finalizing scan '{_buffer}'.")
            FinalizeScan("IdleTimeout")
        End If
    End Sub

End Class
