Imports System.Windows
Imports System.Windows.Controls
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk

    Partial Public Class AdminScreen
        Inherits Window

        Public Sub New()
            InitializeComponent()
            LoadSettingsIntoUi()
        End Sub
        Private Sub LoadSettingsIntoUi()
            ' Passcode length
            If AppSettings.PasscodeLength > 0 Then
                SelectComboBoxItemByContent(PasscodeLengthComboBox, AppSettings.PasscodeLength.ToString())
            End If

            ' Workflow
            If Not String.IsNullOrWhiteSpace(AppSettings.SelectedWorkFlow) Then
                SelectComboBoxItemByContent(WorkflowComboBox, AppSettings.SelectedWorkFlow)
            End If

            ' Style
            If Not String.IsNullOrWhiteSpace(AppSettings.SelectedStyle) Then
                SelectComboBoxItemByContent(StyleComboBox, AppSettings.SelectedStyle)
            End If
        End Sub
        Private Sub SelectComboBoxItemByContent(cb As ComboBox, content As String)
            For Each obj In cb.Items
                Dim item = TryCast(obj, ComboBoxItem)
                If item IsNot Nothing AndAlso String.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase) Then
                    cb.SelectedItem = item
                    Exit For
                End If
            Next
        End Sub
        Private Sub LockerSetup_Click(sender As Object, e As RoutedEventArgs)
            Dim w As New LockerSetupWindow With {.Owner = Me}
            w.ShowDialog()
        End Sub
        Private Sub ControllerSetup_Click(sender As Object, e As RoutedEventArgs)
            Dim w As New ControllerSetupWindow With {.Owner = Me}
            w.ShowDialog()
        End Sub
        Private Sub ExitApp_Click(sender As Object, e As RoutedEventArgs)
            Application.Current.Shutdown()
        End Sub
        Private Sub ExitButton_Click(sender As Object, e As RoutedEventArgs)
            ' Save selected values to AppSettings

            Dim selectedPasscodeLengthItem = TryCast(PasscodeLengthComboBox.SelectedItem, ComboBoxItem)
            If selectedPasscodeLengthItem IsNot Nothing Then
                Dim n As Integer
                If Integer.TryParse(selectedPasscodeLengthItem.Content?.ToString(), n) Then
                    AppSettings.PasscodeLength = n
                End If
            End If

            Dim selectedWorkflowItem = TryCast(WorkflowComboBox.SelectedItem, ComboBoxItem)
            If selectedWorkflowItem IsNot Nothing Then
                AppSettings.SelectedWorkFlow = selectedWorkflowItem.Content?.ToString()
            End If

            Dim selectedStyleItem = TryCast(StyleComboBox.SelectedItem, ComboBoxItem)
            If selectedStyleItem IsNot Nothing Then
                AppSettings.SelectedStyle = selectedStyleItem.Content?.ToString()
            End If

            Me.Close()
        End Sub

    End Class

End Namespace

