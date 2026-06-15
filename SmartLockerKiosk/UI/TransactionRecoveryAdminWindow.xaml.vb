Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.VisualBasic

Namespace SmartLockerKiosk

    Partial Public Class TransactionRecoveryAdminWindow
        Inherits Window

        Public Property ActorId As String = "Admin:Unknown"

        Private ReadOnly _rows As New ObservableCollection(Of IncompleteTransactionRow)
        Private ReadOnly _recovery As New LockerTransactionRecoveryService()

        Public Sub New()
            InitializeComponent()
            RecoveryGrid.ItemsSource = _rows
        End Sub
        Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            Await LoadRowsAsync()
        End Sub
        Private Async Function LoadRowsAsync() As Task

            _rows.Clear()

            Dim rows = Await _recovery.LoadIncompleteTransactionRowsAsync(200)

            For Each row In rows
                _rows.Add(row)
            Next

        End Function
        Private Function SelectedRow() As IncompleteTransactionRow
            Return TryCast(RecoveryGrid.SelectedItem, IncompleteTransactionRow)
        End Function
        Private Function RequireSelectedRow() As IncompleteTransactionRow
            Dim row = SelectedRow()

            If row Is Nothing Then
                MessageBox.Show(
                    "Select one transaction first.",
                    "Transaction Recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information)
            End If

            Return row

        End Function
        Private Async Sub Refresh_Click(sender As Object, e As RoutedEventArgs)
            Await LoadRowsAsync()
        End Sub
        Private Async Sub Details_Click(sender As Object, e As RoutedEventArgs)
            Dim row = RequireSelectedRow()
            If row Is Nothing Then Return

            Dim detail = Await _recovery.GetJournalDetailAsync(row.Id)

            MessageBox.Show(
                detail,
                $"Transaction {row.Id}",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
        End Sub
        Private Async Sub RetryAck_Click(sender As Object, e As RoutedEventArgs)
            Dim row = RequireSelectedRow()
            If row Is Nothing Then Return

            Dim confirm = MessageBox.Show(
                $"Retry backend ACK for transaction {row.Id}?" & Environment.NewLine &
                Environment.NewLine &
                "This will not reopen the locker. If backend retry is not configured, the transaction will remain unresolved.",
                "Retry ACK",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning)

            If confirm <> MessageBoxResult.Yes Then Return

            Dim succeeded = Await _recovery.RetryAckForJournalAsync(row.Id)
            Await LoadRowsAsync()

            MessageBox.Show(
                If(succeeded,
                   "ACK retry succeeded.",
                   "ACK retry did not succeed. The transaction remains available for review."),
                "Retry ACK",
                MessageBoxButton.OK,
                If(succeeded, MessageBoxImage.Information, MessageBoxImage.Warning))
        End Sub
        Private Async Sub NeedsReview_Click(sender As Object, e As RoutedEventArgs)
            Dim row = RequireSelectedRow()
            If row Is Nothing Then Return

            Dim reason = Interaction.InputBox(
                "Enter the review reason.",
                "Needs Review",
                "Marked for admin review.")

            If String.IsNullOrWhiteSpace(reason) Then Return

            Dim succeeded = Await _recovery.MarkJournalNeedsReconciliationAsync(row.Id, reason.Trim())
            Await LoadRowsAsync()

            MessageBox.Show(
                If(succeeded, "Transaction marked for review.", "Transaction could not be updated."),
                "Needs Review",
                MessageBoxButton.OK,
                If(succeeded, MessageBoxImage.Information, MessageBoxImage.Warning))
        End Sub
        Private Async Sub MarkResolved_Click(sender As Object, e As RoutedEventArgs)
            Dim row = RequireSelectedRow()
            If row Is Nothing Then Return

            Dim note = Interaction.InputBox(
                "Enter the resolution note. Use this only after confirming the physical and local state.",
                "Mark Resolved",
                "Resolved by admin after local review.")

            If String.IsNullOrWhiteSpace(note) Then Return

            Dim confirm = MessageBox.Show(
                $"Mark transaction {row.Id} as resolved?" & Environment.NewLine &
                Environment.NewLine &
                "This is a local administrative resolution and does not update backend custody state.",
                "Confirm Resolution",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning)

            If confirm <> MessageBoxResult.Yes Then Return

            Dim succeeded = Await _recovery.MarkJournalResolvedAsync(
                row.Id,
                If(String.IsNullOrWhiteSpace(ActorId), "Admin:Unknown", ActorId),
                note.Trim())

            Await LoadRowsAsync()

            MessageBox.Show(
                If(succeeded, "Transaction marked resolved.", "Transaction could not be resolved."),
                "Mark Resolved",
                MessageBoxButton.OK,
                If(succeeded, MessageBoxImage.Information, MessageBoxImage.Warning))
        End Sub
        Private Async Sub IntegrityReport_Click(sender As Object, e As RoutedEventArgs)
            Dim report = Await _recovery.BuildLocalIntegrityReportAsync()

            MessageBox.Show(
                report,
                "Local Integrity Report",
                MessageBoxButton.OK,
                MessageBoxImage.Information)
        End Sub
        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            Close()
        End Sub

    End Class

End Namespace
