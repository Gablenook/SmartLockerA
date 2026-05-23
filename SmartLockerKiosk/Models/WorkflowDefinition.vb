Public Class WorkflowDefinition

    Public Property WorkflowKey As String

    Public Property DisplayName As String

    ' User-facing button label for the home screen
    Public Property HomeButtonLabel As String

    Public Property Mode As String

    Public Property Steps As List(Of WorkflowStepDefinition)

    Public Property Options As Dictionary(Of String, String) =
        New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    ' Internal workflow behavior identifier
    Public Property WorkflowAction As String = ""

End Class
