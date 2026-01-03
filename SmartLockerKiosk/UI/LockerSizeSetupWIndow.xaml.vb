Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk
    Partial Public Class LockerSizeSetupWindow
        Inherits Window

        Private ReadOnly _rows As New ObservableCollection(Of LockerSize)()

        Public Sub New()
            InitializeComponent()
            SizesGrid.ItemsSource = _rows
        End Sub

        Private Sub LockerSizeSetupWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            LoadFromDb()
        End Sub

        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub

        Private Sub LoadFromDb()
            _rows.Clear()

            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim list = db.LockerSizes.AsNoTracking().
                OrderBy(Function(x) x.SortOrder).
                ThenBy(Function(x) x.SizeCode).
                ToList()

                For Each item In list
                    _rows.Add(item)
                Next
            End Using

            StatusText.Text = $"Loaded {_rows.Count} sizes."
        End Sub
        Private Sub Add_Click(sender As Object, e As RoutedEventArgs)
            Dim nextSort As Integer = If(_rows.Count = 0, 1, _rows.Max(Function(x) x.SortOrder) + 1)

            _rows.Add(New LockerSize With {
            .SizeCode = "",
            .DisplayName = "",
            .WidthIn = 0D,
            .HeightIn = 0D,
            .DepthIn = 0D,
            .SortOrder = nextSort,
            .IsEnabled = True
        })

            SizesGrid.SelectedIndex = _rows.Count - 1
            SizesGrid.ScrollIntoView(SizesGrid.SelectedItem)
            StatusText.Text = "Added new size row (fill in SizeCode, dimensions)."
        End Sub
        Private Sub Delete_Click(sender As Object, e As RoutedEventArgs)
            Dim sel = TryCast(SizesGrid.SelectedItem, LockerSize)
            If sel Is Nothing Then
                StatusText.Text = "Select a row to delete."
                Return
            End If

            _rows.Remove(sel)
            StatusText.Text = "Row removed (click Save to commit)."
        End Sub
        Private Sub Save_Click(sender As Object, e As RoutedEventArgs)
            Dim validationError = ValidateRows()
            If Not String.IsNullOrWhiteSpace(validationError) Then
                StatusText.Text = validationError
                Return
            End If

            Using db = DatabaseBootstrapper.BuildDbContext()

                ' Replace-all strategy: simplest, avoids tricky upserts since key is SizeCode.
                ' If you prefer upsert-by-key, we can do that too.
                Dim existing = db.LockerSizes.ToList()
                db.LockerSizes.RemoveRange(existing)
                db.SaveChanges()

                ' Normalize + insert
                For Each r In _rows
                    Dim normalized = New LockerSize With {
                    .SizeCode = NormalizeSizeCode(r.SizeCode),
                    .DisplayName = (If(r.DisplayName, "")).Trim(),
                    .WidthIn = r.WidthIn,
                    .HeightIn = r.HeightIn,
                    .DepthIn = r.DepthIn,
                    .SortOrder = r.SortOrder,
                    .IsEnabled = r.IsEnabled
                }
                    db.LockerSizes.Add(normalized)
                Next

                db.SaveChanges()
            End Using

            LoadFromDb()
            StatusText.Text = "Saved."
        End Sub
        Private Function ValidateRows() As String
            If _rows.Count = 0 Then
                Return "Add at least one size."
            End If

            Dim codes = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For Each r In _rows
                Dim code = NormalizeSizeCode(r.SizeCode)
                If code.Length = 0 Then Return "SizeCode is required."
                If Not codes.Add(code) Then Return $"Duplicate SizeCode: {code}"

                If r.WidthIn <= 0 OrElse r.HeightIn <= 0 OrElse r.DepthIn <= 0 Then
                    Return $"All dimensions must be > 0 for {code}."
                End If
            Next

            Return Nothing
        End Function
        Private Function NormalizeSizeCode(v As String) As String
            Return (If(v, "")).Trim().ToUpperInvariant()
        End Function
        Private Sub MoveUp_Click(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(CType(sender, FrameworkElement)?.DataContext, LockerSize)
            If row Is Nothing Then Return

            Dim idx = _rows.IndexOf(row)
            If idx <= 0 Then Return

            _rows.Move(idx, idx - 1)
            RenumberSortOrder()
            StatusText.Text = "Reordered (click Save to commit)."
        End Sub
        Private Sub MoveDown_Click(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(CType(sender, FrameworkElement)?.DataContext, LockerSize)
            If row Is Nothing Then Return

            Dim idx = _rows.IndexOf(row)
            If idx < 0 OrElse idx >= _rows.Count - 1 Then Return

            _rows.Move(idx, idx + 1)
            RenumberSortOrder()
            StatusText.Text = "Reordered (click Save to commit)."
        End Sub
        Private Sub RenumberSortOrder()
            For i = 0 To _rows.Count - 1
                _rows(i).SortOrder = i + 1
            Next
            SizesGrid.Items.Refresh()
        End Sub

    End Class
End Namespace
