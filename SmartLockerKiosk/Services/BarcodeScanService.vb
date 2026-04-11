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

    Private _ignoreLeadingLf As Boolean = False

    Public Property ScanTimeoutMs As Integer = 350
    Public Property DuplicateSuppressMs As Integer = 1500
    Public Property MinLength As Integer = 3
    Public Property MaxLength As Integer = 128
    Public Property IsEnabled As Boolean = True

    ''' <summary>
    ''' Optional custom validator. If supplied, it decides whether a completed scan is accepted.
    ''' If Nothing, the built-in basic validation rules are used.
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
        Dim now = DateTime.UtcNow

        ' If a CR just finalized a scan, ignore the immediate LF that often follows.
        If _ignoreLeadingLf AndAlso ch = ControlChars.Lf Then
            _ignoreLeadingLf = False
            RaiseEvent Trace("Ignored LF following CR terminator.")
            Return
        End If

        _ignoreLeadingLf = False

        ' Timeout clears partial scan
        If _lastCharUtc <> DateTime.MinValue AndAlso
           (now - _lastCharUtc).TotalMilliseconds > ScanTimeoutMs Then

            If _buffer.Length > 0 Then
                RaiseEvent Trace($"Buffer timeout after {(now - _lastCharUtc).TotalMilliseconds:F0} ms. Clearing partial scan '{_buffer}'.")
            End If

            _buffer.Clear()
        End If

        _lastCharUtc = now

        ' Terminator -> finalize
        If ch = ControlChars.Cr Then
            FinalizeScan("CR")
            _ignoreLeadingLf = True
            Return
        End If

        If ch = ControlChars.Lf Then
            FinalizeScan("LF")
            Return
        End If

        ' Reject embedded control characters immediately
        If Char.IsControl(ch) Then
            Dim raw = _buffer.ToString()
            _buffer.Clear()
            RaiseEvent ScanRejected("Invalid control character received during scan.", raw)
            RaiseEvent Trace("Rejected scan due to embedded control character.")
            Return
        End If

        ' Length protection
        If _buffer.Length >= MaxLength Then
            Dim raw = _buffer.ToString()
            _buffer.Clear()

            RaiseEvent ScanRejected($"Scan too long (>{MaxLength} characters).", raw)
            RaiseEvent Trace($"Rejected scan because max length {MaxLength} was exceeded.")
            Return
        End If

        _buffer.Append(ch)
    End Sub

    Private Sub FinalizeScan(source As String)
        Dim raw = _buffer.ToString()
        _buffer.Clear()

        Dim text = raw.Trim()

        If String.IsNullOrWhiteSpace(text) Then
            RaiseEvent Trace($"Empty scan ignored ({source}).")
            Return
        End If

        Dim validation = ValidateScan(text)

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

        If Not ContainsOnlyAllowedCharacters(text) Then
            Return ScanValidationResult.Invalid("Scan contains disallowed characters.")
        End If

        If Validator IsNot Nothing Then
            Dim customResult = Validator.Invoke(text)
            If customResult Is Nothing Then
                Return ScanValidationResult.Invalid("Validator returned no result.")
            End If

            Return customResult
        End If

        Return ScanValidationResult.Valid()
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

    Private Function ContainsOnlyAllowedCharacters(value As String) As Boolean
        For Each ch As Char In value
            If Char.IsLetterOrDigit(ch) Then Continue For
            If ch = "-"c OrElse ch = "_"c OrElse ch = "."c Then Continue For
            Return False
        Next

        Return True
    End Function

    Private Function IsDuplicate(value As String) As Boolean
        If value <> _lastScan Then Return False

        Dim age = (DateTime.UtcNow - _lastScanUtc).TotalMilliseconds
        Return age <= DuplicateSuppressMs
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
        _lastScan = ""
        _lastScanUtc = DateTime.MinValue
        _ignoreLeadingLf = False
        RaiseEvent Trace("Barcode scanner state fully reset.")
    End Sub

End Class

