Imports System.Linq
Imports Microsoft.EntityFrameworkCore

Public Class LockerAssignmentService
    Public Function SelectNextAvailableLockerNumber(sizeCode As String, Optional preferredZone As String = Nothing) As String
        Dim code = (If(sizeCode, "")).Trim().ToUpperInvariant()
        If code.Length = 0 Then Return ""

        Using db = DatabaseBootstrapper.BuildDbContext()

            ' Assumptions about your Lockers table based on your usage:
            ' - LockerNumber (string) - door label
            ' - SizeCode (string)     - A/B/C...
            ' - IsEnabled (bool)      - already in your UI rows earlier
            ' - Zone (string)         - optional
            ' NOTE: Out-of-service / occupied are NOT shown in your snippet,
            '       so we treat IsEnabled as "in service" for now.

            Dim q = db.Lockers.AsNoTracking().Where(Function(l) l.IsEnabled)

            ' Filter by size if you have it
            q = q.Where(Function(l) l.SizeCode IsNot Nothing AndAlso l.SizeCode.Trim().ToUpper() = code)

            ' Optional: zone preference
            If Not String.IsNullOrWhiteSpace(preferredZone) Then
                Dim z = preferredZone.Trim()
                q = q.OrderByDescending(Function(l) l.Zone = z).ThenBy(Function(l) l.LockerNumber)
            Else
                q = q.OrderBy(Function(l) l.LockerNumber)
            End If

            ' TODO occupancy: if you have a table/field for occupancy, join/filter here.
            ' For now we assume "available" if enabled and not otherwise tracked.

            Dim locker = q.FirstOrDefault()
            If locker Is Nothing Then Return ""

            Return (If(locker.LockerNumber, "")).Trim()
        End Using
    End Function

End Class
