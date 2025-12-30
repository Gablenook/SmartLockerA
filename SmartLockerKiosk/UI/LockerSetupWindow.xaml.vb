Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Windows
Imports System.Windows.Data
Imports System.Windows.Input
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Namespace SmartLockerKiosk
    Partial Public Class LockerSetupWindow

        ' The grid edits THIS collection only
        Private ReadOnly _rows As New ObservableCollection(Of LockerRow)
        Public Property AdminActorID As String
        Public Sub New()
            InitializeComponent()

            ' Bind once; never rebind to a different collection
            LockersGrid.ItemsSource = _rows
            StatusText.Text = "Loading lockers..."
            LoadLockersFromDb()
        End Sub

        Private Sub Load_Click(sender As Object, e As RoutedEventArgs)
            LoadLockersFromDb()
        End Sub
        Private Sub Add_Click(sender As Object, e As RoutedEventArgs)

            Dim defaultSizeCode As String = "A" ' safe fallback

            Try
                Using db = DatabaseBootstrapper.BuildDbContext()

                    ' If you have LockerSizes in your context, use it.
                    ' If you named it differently, adjust db.LockerSizes accordingly.
                    Dim firstEnabled =
                db.LockerSizes.
                   AsNoTracking().
                   Where(Function(s) s.IsEnabled).
                   OrderBy(Function(s) s.SortOrder).
                   Select(Function(s) s.SizeCode).
                   FirstOrDefault()

                    If Not String.IsNullOrWhiteSpace(firstEnabled) Then
                        defaultSizeCode = firstEnabled.Trim().ToUpperInvariant()
                    End If
                End Using
            Catch
                ' If LockerSizes table/db set isn't present yet, ignore and keep "A"
            End Try

            _rows.Add(New LockerRow With {
        .LockerId = 0,
        .Branch = "A",
        .RelayId = 1,
        .LockerNumber = NextSuggestedLockerNumber(),
        .SizeCode = defaultSizeCode,
        .Zone = "",
        .IsEnabled = True
    })
            Dim actionId As String = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = AdminActorID,
    .AffectedComponent = "LockerSetupWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = actionId,
    .ReasonCode = "LockerRowAdded"
})



            StatusText.Text = "Added a new locker row. Set Branch, RelayId, LockerNumber, then Save."

        End Sub

        Private Sub Remove_Click(sender As Object, e As RoutedEventArgs)
            Dim selected = LockersGrid.SelectedItems.Cast(Of LockerRow).ToList()
            If selected.Count = 0 Then
                StatusText.Text = "No rows selected."
                Return
            End If

            For Each r In selected
                _rows.Remove(r)
            Next

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
    .EventType = Audit.AuditEventType.PolicyConfigurationChange,
    .ActorType = Audit.ActorType.Admin,
    .ActorId = AdminActorID,
    .AffectedComponent = "LockerSetupWindow",
    .Outcome = Audit.AuditOutcome.Success,
    .CorrelationId = actionId,
    .ReasonCode = $"LockerRowsRemoved:Count={selected.Count}"
})


            StatusText.Text = $"Removed {selected.Count} row(s) from the grid. Click Save to commit."
        End Sub
        Private Sub Save_Click(sender As Object, e As RoutedEventArgs)

            Dim actionId As String = Guid.NewGuid().ToString("N")

            ' 1) Commit any in-progress edits so values land in _rows
            LockersGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, True)
            LockersGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, True)

            Dim view = CollectionViewSource.GetDefaultView(LockersGrid.ItemsSource)
            Dim editable = TryCast(view, IEditableCollectionView)
            If editable IsNot Nothing Then
                If editable.IsEditingItem Then editable.CommitEdit()
                If editable.IsAddingNew Then editable.CommitNew()
            End If

            ' 2) Validate
            Dim err = ValidateRows()
            If err IsNot Nothing Then
                MessageBox.Show(err)

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveDenied:ValidationFailed"
        })
                Return
            End If

            If _rows.Count = 0 Then
                MessageBox.Show("Nothing to save.")

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Denied,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveDenied:Empty"
        })
                Return
            End If

            Try
                Dim addedCount As Integer = 0
                Dim updatedCount As Integer = 0
                Dim deletedCount As Integer = 0

                Using db = DatabaseBootstrapper.BuildDbContext()

                    ' Existing lockers keyed by id
                    Dim existing = db.Lockers.ToList().ToDictionary(Function(l) l.LockerId)

                    ' IDs present in grid
                    Dim idsInGrid As HashSet(Of Integer) =
                _rows.Where(Function(r) r IsNot Nothing AndAlso r.LockerId > 0).
                      Select(Function(r) r.LockerId).
                      ToHashSet()

                    ' Delete those removed from grid
                    If existing.Count > 0 Then
                        Dim toDelete = existing.Values.
                    Where(Function(l) Not idsInGrid.Contains(l.LockerId)).
                    ToList()

                        deletedCount = toDelete.Count
                        If deletedCount > 0 Then
                            db.Lockers.RemoveRange(toDelete)
                        End If
                    End If

                    ' Upsert rows
                    For Each r In _rows
                        If r Is Nothing Then Continue For

                        Dim entity As Locker = Nothing
                        Dim isNew As Boolean = (r.LockerId <= 0 OrElse Not existing.TryGetValue(r.LockerId, entity))

                        If isNew Then
                            entity = New Locker()
                            db.Lockers.Add(entity)
                            addedCount += 1
                        Else
                            ' Count as update if any meaningful field differs
                            Dim nb = NormalizeBranch(r.Branch)
                            Dim nln = NormalizeLockerNumber(r.LockerNumber)
                            Dim nsc = NormalizeSizeCode(r.SizeCode)
                            Dim nz = If(r.Zone, "")
                            Dim nen = r.IsEnabled

                            If Not String.Equals(entity.Branch, nb, StringComparison.OrdinalIgnoreCase) OrElse
                       entity.RelayId <> r.RelayId OrElse
                       Not String.Equals(entity.LockerNumber, nln, StringComparison.OrdinalIgnoreCase) OrElse
                       Not String.Equals(entity.SizeCode, nsc, StringComparison.OrdinalIgnoreCase) OrElse
                       Not String.Equals(If(entity.Zone, ""), nz, StringComparison.OrdinalIgnoreCase) OrElse
                       entity.IsEnabled <> nen Then
                                updatedCount += 1
                            End If
                        End If

                        entity.Branch = NormalizeBranch(r.Branch)
                        entity.RelayId = r.RelayId
                        entity.LockerNumber = NormalizeLockerNumber(r.LockerNumber)
                        entity.SizeCode = NormalizeSizeCode(r.SizeCode)
                        entity.Zone = If(r.Zone, "")
                        entity.IsEnabled = r.IsEnabled
                    Next

                    db.SaveChanges()
                End Using

                LoadLockersFromDb()
                StatusText.Text = "Saved."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"LockerConfigSaved:Added={addedCount};Updated={updatedCount};Deleted={deletedCount}"
        })

            Catch
                MessageBox.Show("Save failed.")

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigSaveFailed"
        })
            End Try

        End Sub

        Private Sub Close_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub
        Private Sub TextBox_GotFocus(sender As Object, e As RoutedEventArgs)
            KeyboardHelper.ShowTouchKeyboard()
        End Sub
        Private Sub LockerSetupWindow_Closed(sender As Object, e As EventArgs) Handles Me.Closed
            KeyboardHelper.HideTouchKeyboard()
        End Sub

        ' ---------- DB Load ----------
        Private Sub LoadLockersFromDb()

            Dim actionId As String = Guid.NewGuid().ToString("N")

            Try
                _rows.Clear()

                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim lockers = db.Lockers.AsNoTracking().
                OrderBy(Function(x) x.LockerNumber).
                ToList()

                    For Each l In lockers
                        _rows.Add(New LockerRow With {
                    .LockerId = l.LockerId,
                    .Branch = l.Branch,
                    .RelayId = l.RelayId,
                    .LockerNumber = l.LockerNumber,
                    .SizeCode = l.SizeCode,
                    .Zone = l.Zone,
                    .IsEnabled = l.IsEnabled
                })
                    Next
                End Using

                StatusText.Text = $"Loaded {_rows.Count} locker(s)."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Success,
            .CorrelationId = actionId,
            .ReasonCode = $"LockerConfigLoaded:Count={_rows.Count}"
        })

            Catch
                StatusText.Text = "ERROR loading lockers."

                Audit.AuditServices.SafeLog(New Audit.AuditEvent With {
            .EventType = Audit.AuditEventType.PolicyConfigurationChange,
            .ActorType = Audit.ActorType.Admin,
            .ActorId = AdminActorID,
            .AffectedComponent = "LockerSetupWindow",
            .Outcome = Audit.AuditOutcome.Error,
            .CorrelationId = actionId,
            .ReasonCode = "LockerConfigLoadFailed"
        })
            End Try

        End Sub


        ' ---------- Helpers ----------
        Private Shared Function NormalizeBranch(branch As String) As String
            Dim b = (If(branch, "")).Trim().ToUpperInvariant()
            If b <> "A" AndAlso b <> "B" Then Return b ' validation will catch
            Return b
        End Function
        Private Shared Function NormalizeSizeCode(sizeCode As String) As String
            Return (If(sizeCode, "")).Trim().ToUpperInvariant()
        End Function
        Private Shared Function NormalizeLockerNumber(lockerNumber As String) As String
            Return (If(lockerNumber, "")).Trim()
        End Function
        Private Function NextSuggestedLockerNumber() As String
            If _rows Is Nothing OrElse _rows.Count = 0 Then
                Return "001"
            End If

            Dim maxNum As Integer = 0

            For Each r In _rows
                If r Is Nothing Then Continue For

                Dim s = (If(r.LockerNumber, "")).Trim()
                If s.Length = 0 Then Continue For

                ' Pull trailing digits if present (e.g., "A-012" -> 12, "012" -> 12)
                Dim i As Integer = s.Length - 1
                While i >= 0 AndAlso Char.IsDigit(s(i))
                    i -= 1
                End While

                Dim digits = s.Substring(i + 1)
                If digits.Length = 0 Then Continue For

                Dim n As Integer
                If Integer.TryParse(digits, n) Then
                    If n > maxNum Then maxNum = n
                End If
            Next

            Dim nextNum = maxNum + 1
            Return nextNum.ToString("000")  ' 3-digit suggestion
        End Function

        Private Function ValidateRows() As String

            Dim usedAddr As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim usedLockerNumber As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            ' Pull enabled size codes from DB (if available). If not available, we won't hard-fail size codes here.
            Dim enabledSizeCodes As HashSet(Of String) = Nothing
            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim codes =
                        db.LockerSizes.
                           AsNoTracking().
                           Where(Function(s) s.IsEnabled).
                           Select(Function(s) s.SizeCode).
                           ToList()

                    enabledSizeCodes = New HashSet(Of String)(
                        codes.Select(Function(c) (If(c, "")).Trim().ToUpperInvariant()).
                              Where(Function(c) c.Length > 0),
                        StringComparer.OrdinalIgnoreCase
                    )
                End Using
            Catch
                enabledSizeCodes = Nothing
            End Try

            For Each r In _rows
                If r Is Nothing Then Continue For

                Dim branch = NormalizeBranch(r.Branch)
                If branch <> "A" AndAlso branch <> "B" Then
                    Return $"LockerNumber '{r.LockerNumber}': Branch must be A or B."
                End If

                If r.RelayId < 1 OrElse r.RelayId > 200 Then
                    Return $"LockerNumber '{r.LockerNumber}': RelayId must be 1–200."
                End If

                Dim addrKey = $"{branch}:{r.RelayId}"
                If Not usedAddr.Add(addrKey) Then
                    Return $"Duplicate Branch + RelayId: {branch}-{r.RelayId}"
                End If

                Dim ln = NormalizeLockerNumber(r.LockerNumber)
                If ln.Length = 0 Then
                    Return "LockerNumber is required."
                End If
                If Not usedLockerNumber.Add(ln) Then
                    Return $"Duplicate LockerNumber: {ln}"
                End If

                Dim sc = NormalizeSizeCode(r.SizeCode)
                If sc.Length = 0 Then
                    Return $"SizeCode is required for LockerNumber '{ln}'."
                End If

                ' If we successfully loaded enabled codes from DB, enforce membership.
                If enabledSizeCodes IsNot Nothing AndAlso enabledSizeCodes.Count > 0 Then
                    If Not enabledSizeCodes.Contains(sc) Then
                        Return $"Invalid or disabled SizeCode '{sc}' for LockerNumber '{ln}'."
                    End If
                End If
            Next

            Return Nothing

        End Function


    End Class
End Namespace