Imports System.ComponentModel.DataAnnotations

Namespace SmartLockerKiosk

    Public Class KioskState
        <Key>
        Public Property KioskId As String          ' PK (same value as AppSettings.KioskID)
        Public Property LocationId As String       ' optional but useful
        Public Property IsCommissioned As Boolean
        Public Property CommissionedUtc As DateTime?
        Public Property CommissionedBy As String
        Public Property LastUpdatedUtc As DateTime = DateTime.UtcNow
    End Class

End Namespace

