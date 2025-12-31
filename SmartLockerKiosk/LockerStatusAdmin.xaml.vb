Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Controls
Imports Microsoft.EntityFrameworkCore

Partial Public Class LockerStatusAdmin
    Inherits Window

    Public Property AdminActorId As String = "Admin:Unknown"

    Private ReadOnly _rows As New ObservableCollection(Of LockerState)

    Public Sub New()
        InitializeComponent()

        GridStatus.ItemsSource = _rows

        ' Populate ComboBox sources for DataGridComboBoxColumns (Occupancy, Lock)
        Dim occupancyValues = [Enum].GetValues(GetType(OccupancyState)).Cast(Of OccupancyState)().ToList()
        Dim lockValues = [Enum].GetValues(GetType(LockState)).Cast(Of LockState)().ToList()

        ' Columns: 5=Occupancy, 6=Lock based on the XAML order above
        Dim occCol = TryCast(GridStatus.Columns(5), DataGridComboBoxColumn)
        If occCol IsNot Nothing Then occCol.ItemsSource = occupancyValues

        Dim lockCol = TryCast(GridStatus.Columns(6), DataGridComboBoxColumn)
        If lockCol IsNot Nothing Then lockCol.ItemsSource = lockValues
    End Sub
    Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
        Me.Close()
    End Sub

End Class


