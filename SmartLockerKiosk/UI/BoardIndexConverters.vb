Imports System
Imports System.Globalization
Imports System.Windows.Data

Namespace Global.SmartLockerKiosk
    Public Class OneBasedIndexConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
            Dim idx As Integer = 0
            If value IsNot Nothing Then Integer.TryParse(value.ToString(), idx)
            Return (idx + 1).ToString()
        End Function

        Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class
End Namespace
