Public Class AuthorizeRequest
    Public Property credentialkey As String
    Public Property badgeId As String
    Public Property kioskId As String
    Public Property siteCode As String
    Public Property clientCode As String
    Public Property credentialType As String   ' Badge | Pin | Barcode | QR
    Public Property purpose As String          ' Pickup | Deliver | Admin
    Public Property locationId As String
    Public Property timestampUtc As DateTime
    Public Property requestId As String
End Class

