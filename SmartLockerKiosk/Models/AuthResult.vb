Public Class AuthResult
    Public Property IsAuthorized As Boolean
    Public Property Message As String

    ' What the kiosk should do next
    Public Property Purpose As AuthPurpose
    Public Property NextAction As String   ' e.g. "OPEN_LOCKER", "PROMPT_WORK_ORDER", "SELECT_LOCKER"

    ' Common identity fields (optional)
    Public Property UserId As String
    Public Property DisplayName As String

    ' Pickup / DayUse: if the server already knows/assigns a locker
    Public Property LockerNumber As String    ' alphanumeric; Nothing/"" means not assigned

    ' Pickup: optional but useful for logging
    Public Property WorkOrderNumber As String
    Public Property WorkOrders As List(Of WorkOrderAuthItem) = New List(Of WorkOrderAuthItem)()
    Public Property SessionToken As String
End Class
