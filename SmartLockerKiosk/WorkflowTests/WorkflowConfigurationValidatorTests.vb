Imports System.IO
Imports System.Text.Json
Imports Xunit

Public Class WorkflowConfigurationValidatorTests
    <Theory>
    <InlineData("tsa-workflow.json")>
    <InlineData("shaw-workflow.json")>
    <InlineData("ryder-workflow.json")>
    Public Sub CurrentWorkflowConfigurationsAreValid(fileName As String)
        Dim configPath = IO.Path.Combine(AppContext.BaseDirectory, "Configs", fileName)
        Dim config = JsonSerializer.Deserialize(Of KioskWorkflowConfiguration)(
            File.ReadAllText(configPath),
            New JsonSerializerOptions With {.PropertyNameCaseInsensitive = True})

        WorkflowConfigurationValidator.Validate(config)
    End Sub

    <Fact>
    Public Sub RejectsCycles()
        Dim config = CreateConfig(
            New WorkflowStepDefinition With {
                .StepKey = "credential_scan",
                .InputType = "scan_or_keypad",
                .NextStepKey = "work_order_scan"
            },
            New WorkflowStepDefinition With {
                .StepKey = "work_order_scan",
                .InputType = "scan_or_keypad",
                .NextStepKey = "credential_scan"
            })

        Assert.Throws(Of InvalidOperationException)(
            Sub() WorkflowConfigurationValidator.Validate(config))
    End Sub

    <Fact>
    Public Sub RejectsIllegalTerminalSteps()
        Dim config = CreateConfig(
            New WorkflowStepDefinition With {
                .StepKey = "credential_scan",
                .InputType = "scan_or_keypad",
                .NextStepKey = Nothing
            })

        Assert.Throws(Of InvalidOperationException)(
            Sub() WorkflowConfigurationValidator.Validate(config))
    End Sub

    <Fact>
    Public Sub RejectsMissingValidationProfiles()
        Dim config = CreateConfig(
            New WorkflowStepDefinition With {
                .StepKey = "work_order_scan",
                .InputType = "scan_or_keypad",
                .ValidationProfileKey = "missing",
                .NextStepKey = Nothing
            })

        Assert.Throws(Of InvalidOperationException)(
            Sub() WorkflowConfigurationValidator.Validate(config))
    End Sub

    Private Shared Function CreateConfig(ParamArray steps() As WorkflowStepDefinition) As KioskWorkflowConfiguration
        Dim workflow As New WorkflowDefinition With {
            .WorkflowKey = "test-pickup",
            .DisplayName = "Test",
            .Mode = "package_workflow",
            .WorkflowAction = "pickup",
            .Steps = steps.ToList()
        }

        Return New KioskWorkflowConfiguration With {
            .DefaultWorkflowKey = workflow.WorkflowKey,
            .HomePickupWorkflowKey = workflow.WorkflowKey,
            .EnabledWorkflows = New List(Of WorkflowDefinition) From {workflow},
            .ScanValidationProfiles = New List(Of ScanValidationProfile)()
        }
    End Function
End Class
