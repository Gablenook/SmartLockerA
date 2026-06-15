Public NotInheritable Class WorkflowConfigurationValidator
    Private Shared ReadOnly SupportedModes As New HashSet(Of String)(
        {"package_workflow", "asset_workflow", "day_use"},
        StringComparer.OrdinalIgnoreCase)

    Private Shared ReadOnly SupportedActions As New HashSet(Of String)(
        {"pickup", "stage"},
        StringComparer.OrdinalIgnoreCase)

    Private Shared ReadOnly SupportedInputTypes As New HashSet(Of String)(
        {"scan_only", "scan_or_keypad", "ui_only"},
        StringComparer.OrdinalIgnoreCase)

    Private Shared ReadOnly SupportedStepKeys As New HashSet(Of String)(
        {
            "credential_scan",
            "work_order_scan",
            "asset_scan",
            "defect_decision",
            "size_selection",
            "compartment_assignment",
            "device_checkout"
        },
        StringComparer.OrdinalIgnoreCase)

    Private Sub New()
    End Sub

    Public Shared Sub Validate(config As KioskWorkflowConfiguration)
        If config Is Nothing Then
            Throw New InvalidOperationException("Workflow configuration is missing.")
        End If

        If config.EnabledWorkflows Is Nothing OrElse config.EnabledWorkflows.Count = 0 Then
            Throw New InvalidOperationException("Workflow configuration does not contain any enabled workflows.")
        End If

        Dim profileKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each profile In If(config.ScanValidationProfiles, New List(Of ScanValidationProfile)())
            If profile Is Nothing OrElse String.IsNullOrWhiteSpace(profile.ProfileKey) Then Continue For
            If Not profileKeys.Add(profile.ProfileKey.Trim()) Then
                Throw New InvalidOperationException($"Duplicate scan validation profile '{profile.ProfileKey}'.")
            End If
        Next

        Dim workflowKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each workflow In config.EnabledWorkflows
            ValidateWorkflow(workflow, workflowKeys, profileKeys)
        Next

        RequireConfiguredWorkflow(config.HomePickupWorkflowKey, "HomePickupWorkflowKey", workflowKeys)
        RequireConfiguredWorkflow(config.HomeDeliveryWorkflowKey, "HomeDeliveryWorkflowKey", workflowKeys)

        If Not String.IsNullOrWhiteSpace(config.DefaultWorkflowKey) AndAlso
           Not workflowKeys.Contains(config.DefaultWorkflowKey.Trim()) Then
            Throw New InvalidOperationException(
                $"Default workflow '{config.DefaultWorkflowKey}' is not enabled.")
        End If
    End Sub

    Private Shared Sub ValidateWorkflow(
        workflow As WorkflowDefinition,
        workflowKeys As HashSet(Of String),
        profileKeys As HashSet(Of String))

        If workflow Is Nothing Then
            Throw New InvalidOperationException("Workflow configuration contains a null workflow.")
        End If

        Dim workflowKey As String = If(workflow.WorkflowKey, "").Trim()
        If workflowKey.Length = 0 Then
            Throw New InvalidOperationException("A workflow is missing WorkflowKey.")
        End If

        If Not workflowKeys.Add(workflowKey) Then
            Throw New InvalidOperationException($"Duplicate workflow key '{workflowKey}'.")
        End If

        Dim mode As String = If(workflow.Mode, "").Trim()
        If Not SupportedModes.Contains(mode) Then
            Throw New InvalidOperationException(
                $"Workflow '{workflowKey}' has unsupported mode '{workflow.Mode}'.")
        End If

        Dim action As String = If(workflow.WorkflowAction, "").Trim()
        If Not SupportedActions.Contains(action) Then
            Throw New InvalidOperationException(
                $"Workflow '{workflowKey}' has unsupported action '{workflow.WorkflowAction}'.")
        End If

        If workflow.Steps Is Nothing OrElse workflow.Steps.Count = 0 Then
            Throw New InvalidOperationException($"Workflow '{workflowKey}' has no steps.")
        End If

        Dim stepsByKey As New Dictionary(Of String, WorkflowStepDefinition)(StringComparer.OrdinalIgnoreCase)

        For Each stepDef In workflow.Steps
            If stepDef Is Nothing Then
                Throw New InvalidOperationException($"Workflow '{workflowKey}' contains a null step.")
            End If

            Dim stepKey As String = If(stepDef.StepKey, "").Trim()
            If Not SupportedStepKeys.Contains(stepKey) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' contains unsupported step '{stepDef.StepKey}'.")
            End If

            If stepsByKey.ContainsKey(stepKey) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' contains duplicate step '{stepKey}'.")
            End If

            Dim inputType As String = If(stepDef.InputType, "").Trim()
            If Not SupportedInputTypes.Contains(inputType) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' step '{stepKey}' has unsupported input type '{stepDef.InputType}'.")
            End If

            If Not String.IsNullOrWhiteSpace(stepDef.ValidationProfileKey) AndAlso
               Not profileKeys.Contains(stepDef.ValidationProfileKey.Trim()) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' step '{stepKey}' references missing validation profile '{stepDef.ValidationProfileKey}'.")
            End If

            stepsByKey.Add(stepKey, stepDef)
        Next

        For Each stepDef In workflow.Steps
            If Not String.IsNullOrWhiteSpace(stepDef.NextStepKey) AndAlso
               Not stepsByKey.ContainsKey(stepDef.NextStepKey.Trim()) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' step '{stepDef.StepKey}' points to missing next step '{stepDef.NextStepKey}'.")
            End If
        Next

        Dim visited As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim currentKey As String = workflow.Steps(0).StepKey.Trim()

        While currentKey.Length > 0
            If Not visited.Add(currentKey) Then
                Throw New InvalidOperationException(
                    $"Workflow '{workflowKey}' contains a cycle at step '{currentKey}'.")
            End If

            Dim currentStep = stepsByKey(currentKey)
            currentKey = If(currentStep.NextStepKey, "").Trim()
        End While

        If visited.Count <> stepsByKey.Count Then
            Dim unreachable = stepsByKey.Keys.Where(Function(key) Not visited.Contains(key))
            Throw New InvalidOperationException(
                $"Workflow '{workflowKey}' contains unreachable step(s): {String.Join(", ", unreachable)}.")
        End If

        ValidateTerminalStep(workflowKey, mode, action, workflow.Steps.Last().StepKey)
    End Sub

    Private Shared Sub ValidateTerminalStep(
        workflowKey As String,
        mode As String,
        action As String,
        terminalStepKey As String)

        Dim terminal As String = If(terminalStepKey, "").Trim()
        Dim isValid As Boolean

        If mode.Equals("asset_workflow", StringComparison.OrdinalIgnoreCase) Then
            isValid = If(
                action.Equals("pickup", StringComparison.OrdinalIgnoreCase),
                terminal.Equals("device_checkout", StringComparison.OrdinalIgnoreCase),
                terminal.Equals("compartment_assignment", StringComparison.OrdinalIgnoreCase))
        ElseIf mode.Equals("package_workflow", StringComparison.OrdinalIgnoreCase) Then
            isValid = If(
                action.Equals("pickup", StringComparison.OrdinalIgnoreCase),
                terminal.Equals("work_order_scan", StringComparison.OrdinalIgnoreCase),
                terminal.Equals("size_selection", StringComparison.OrdinalIgnoreCase) OrElse
                terminal.Equals("compartment_assignment", StringComparison.OrdinalIgnoreCase))
        Else
            isValid = True
        End If

        If Not isValid Then
            Throw New InvalidOperationException(
                $"Workflow '{workflowKey}' has illegal terminal step '{terminal}' for {mode}/{action}.")
        End If
    End Sub

    Private Shared Sub RequireConfiguredWorkflow(
        workflowKey As String,
        settingName As String,
        workflowKeys As HashSet(Of String))

        If String.IsNullOrWhiteSpace(workflowKey) Then Return

        If Not workflowKeys.Contains(workflowKey.Trim()) Then
            Throw New InvalidOperationException(
                $"{settingName} references workflow '{workflowKey}', which is not enabled.")
        End If
    End Sub
End Class
