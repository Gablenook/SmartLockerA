Imports System.ComponentModel.DataAnnotations

Namespace SmartLockerKiosk

    Public Class KioskState
        <Key>
        Public Property KioskId As String

        Public Property LocationId As String
        Public Property TenantId As String
        Public Property OrgNodeId As String

        Public Property IsCommissioned As Boolean
        Public Property CommissionedUtc As DateTime?
        Public Property CommissionedBy As String

        Public Property CommissioningSessionId As String
        Public Property KioskToken As String
        Public Property RefreshToken As String

        Public Property KioskName As String
        Public Property OrganizationName As String
        Public Property LogoUri As String
        Public Property SecondaryLogoUri As String
        Public Property AccentColor As String

        Public Property LastHealthRegistrationUtc As DateTime?
        Public Property LastUpdatedUtc As DateTime = DateTime.UtcNow
    End Class

End Namespace
