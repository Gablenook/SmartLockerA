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

    ''' <summary>
    ''' Optional custom validator.
    ''' When Nothing, the service performs only structural validation
    ''' and accepts all non-empty scans within length limits.
    ''' </summary>
    Public Property Validator As Func(Of String, ScanValidationResult)

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
        Dim nowUtc As DateTime = DateTime.UtcNow

        ' If CR already finalized the scan, ignore the immediate LF that often follows.
        If _ignoreLeadingLf AndAlso ch = ControlChars.Lf Then
            _ignoreLeadingLf = False
            RaiseEvent Trace("Ignored LF following CR terminator.")
            Return
        End If

        _ignoreLeadingLf = False

        ' If enough time has passed since the previous character, abandon any partial buffer.
        If _lastCharUtc <> DateTime.MinValue AndAlso
           (nowUtc - _lastCharUtc).TotalMilliseconds > ScanTimeoutMs Then

            If _buffer.Length > 0 Then
                RaiseEvent Trace($"Buffer timeout after {(nowUtc - _lastCharUtc).TotalMilliseconds:F0} ms. Clearing partial scan '{_buffer}'.")
            End If

            _buffer.Clear()
        End If

        _lastCharUtc = nowUtc

        ' Terminators finalize the scan.
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
    End Sub

    Private Sub FinalizeScan(source As String)
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

    '----------------------------------------
    ' VALIDATION
    '----------------------------------------

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

    '----------------------------------------
    ' HELPERS
    '----------------------------------------

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
        _buffer.Clear()
        _lastCharUtc = DateTime.MinValue
        _ignoreLeadingLf = False

        RaiseEvent Trace("Barcode input buffer reset.")
    End Sub

    Public Sub ResetAll()
        _buffer.Clear()
        _lastCharUtc = DateTime.MinValue
        _lastScan = String.Empty
        _lastScanUtc = DateTime.MinValue
        _ignoreLeadingLf = False

        RaiseEvent Trace("Barcode scanner state fully reset.")
    End Sub

End Class
