Imports System
Imports SmartLockerKiosk.SmartLockerKiosk

Public NotInheritable Class TestAuthProvider

    ' In test mode, we’ll return stable, predictable results.
    Public Shared Function Authorize(credential As String, purpose As AuthPurpose) As AuthResult
        Dim c = (If(credential, "")).Trim()
        If c.Length = 0 Then
            Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = "Empty credential."}
        End If

        Select Case purpose
            Case AuthPurpose.AdminAccess
                If IsMatch(c, AppSettings.TestAdminCredential, "ADMIN") Then
                    Return New AuthResult With {
                        .IsAuthorized = True,
                        .Purpose = purpose,
                        .UserId = "admin-test",
                        .DisplayName = "Test Admin",
                        .Message = "OK (TEST)",
                        .NextAction = "OPEN_ADMIN",
                        .WorkOrders = New List(Of WorkOrderAuthItem)()
                    }
                End If

            Case AuthPurpose.DeliveryCourierAuth
                If IsMatch(c, AppSettings.TestCourierCredential, "COURIER") Then
                    Return New AuthResult With {
                        .IsAuthorized = True,
                        .Purpose = purpose,
                        .UserId = "courier-test",
                        .DisplayName = "Test Courier",
                        .Message = "OK (TEST)",
                        .NextAction = "PROMPT_WORK_ORDER",
                        .WorkOrders = New List(Of WorkOrderAuthItem)()
                    }
                End If

            Case AuthPurpose.PickupAccess
                If IsMatch(c, AppSettings.TestPickupCredential, "PICKUP") Then
                    ' Provide pickup work orders already assigned to lockers
                    Dim wos As New List(Of WorkOrderAuthItem) From {
                        New WorkOrderAuthItem With {
                            .WorkOrderNumber = "WO-PICKUP-1001",
                            .TransactionType = "Pickup",
                            .LockerNumber = "A-001"
                        },
                        New WorkOrderAuthItem With {
                            .WorkOrderNumber = "WO-PICKUP-1002",
                            .TransactionType = "Pickup",
                            .LockerNumber = "B-014"
                        }
                    }

                    Return New AuthResult With {
                        .IsAuthorized = True,
                        .Purpose = purpose,
                        .UserId = "pickup-test",
                        .DisplayName = "Test Pickup User",
                        .Message = "OK (TEST)",
                        .NextAction = "PROMPT_WORK_ORDER",
                        .WorkOrders = wos
                    }
                End If
        End Select

        Return New AuthResult With {.IsAuthorized = False, .Purpose = purpose, .Message = "Credential not recognized (TEST)."}
    End Function

    Private Shared Function IsMatch(actual As String, configured As String, fallbackPrefix As String) As Boolean
        Dim a = actual.Trim()
        Dim cfg = (If(configured, "")).Trim()

        If cfg.Length > 0 AndAlso a.Equals(cfg, StringComparison.OrdinalIgnoreCase) Then Return True

        ' fallback: allow “ADMIN…” / “COURIER…” / “PICKUP…”
        If a.StartsWith(fallbackPrefix, StringComparison.OrdinalIgnoreCase) Then Return True

        Return False
    End Function

End Class

