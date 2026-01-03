Imports System.IO
Imports System.Text
Imports System.Text.Json

Public Class PendingCommitStore
    Private ReadOnly _path As String
    Private Shared ReadOnly _opts As New JsonSerializerOptions With {.WriteIndented = True}

    Public Sub New(Optional filePath As String = Nothing)
        If String.IsNullOrWhiteSpace(filePath) Then
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SmartLockerKiosk")
            Directory.CreateDirectory(dir)
            _path = Path.Combine(dir, "pending_delivery_commits.json")
        Else
            _path = filePath
        End If
    End Sub
    Public Function LoadAll() As List(Of PendingDeliveryCommit)
        If Not File.Exists(_path) Then Return New List(Of PendingDeliveryCommit)

        Dim json = File.ReadAllText(_path, Encoding.UTF8)
        If String.IsNullOrWhiteSpace(json) Then Return New List(Of PendingDeliveryCommit)

        Return If(
    JsonSerializer.Deserialize(Of List(Of PendingDeliveryCommit))(json, _opts),
    New List(Of PendingDeliveryCommit)()
)

    End Function
    Public Sub SaveAll(items As List(Of PendingDeliveryCommit))
        Dim json = JsonSerializer.Serialize(items, _opts)
        File.WriteAllText(_path, json, Encoding.UTF8)
    End Sub
    Public Sub Enqueue(item As PendingDeliveryCommit)
        Dim items = LoadAll()
        items.Add(item)
        SaveAll(items)
    End Sub
    Public Sub RemoveByCommitId(commitId As String)
        Dim items = LoadAll()
        items = items.Where(Function(x) Not String.Equals(x.CommitId, commitId, StringComparison.OrdinalIgnoreCase)).ToList()
        SaveAll(items)
    End Sub
    Public Sub Update(item As PendingDeliveryCommit)
        Dim items = LoadAll()
        Dim idx = items.FindIndex(Function(x) String.Equals(x.CommitId, item.CommitId, StringComparison.OrdinalIgnoreCase))
        If idx >= 0 Then
            items(idx) = item
            SaveAll(items)
        End If
    End Sub
End Class
