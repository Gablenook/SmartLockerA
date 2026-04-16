Public Class KioskWorkflowConfiguration
    Public Property DefaultWorkflowKey As String
    Public Property EnabledWorkflows As List(Of WorkflowDefinition) = New List(Of WorkflowDefinition)()
    Public Property ScanValidationProfiles As List(Of ScanValidationProfile) = New List(Of ScanValidationProfile)()
End Class