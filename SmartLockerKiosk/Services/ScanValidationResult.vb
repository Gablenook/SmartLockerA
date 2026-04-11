Public Class ScanValidationResult

    Public Property IsValid As Boolean
    Public Property Reason As String

    Public Shared Function Valid() As ScanValidationResult
        Return New ScanValidationResult With {
            .IsValid = True,
            .Reason = ""
        }
    End Function
    Public Shared Function Invalid(reason As String) As ScanValidationResult
        Return New ScanValidationResult With {
            .IsValid = False,
            .Reason = reason
        }
    End Function

End Class
